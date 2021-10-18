﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Railgun.Api;
using Railgun.BytecodeRuntime;
using Railgun.Grammar;
using Railgun.Grammar.Sweet;
using Railgun.Types;

namespace Railgun.Runtime
{
    public class RailgunRuntime : IRailgunRuntime
    {
        private static string LoadEmbeddedFile(string name)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            using var reader = new StreamReader(stream!);
            return reader.ReadToEnd();
        }
        
        private readonly string _workingDirectory;
        public readonly RailgunEnvironment Globals = new();

        private void NewFn(string name, Func<object[], object> body)
        {
            Globals[name] = new BuiltinClosure(body);
        }
        
        private void NewMacro(string name, Func<object[], object> body)
        {
            Globals[name] = new BuiltinClosure(body, true);
        }
        
        public RailgunRuntime(string workingDirectory = "")
        {
            _workingDirectory = workingDirectory;

            Globals["true"] = true;
            Globals["false"] = false;
            Globals["nil"] = Nil.Value;
            
            // types
            Globals["Seq"] = typeof(Seq);
            Globals["Int"] = typeof(int);
            Globals["Float"] = typeof(float);
            Globals["Double"] = typeof(double);
            Globals["String"] = typeof(string);
            Globals["Bool"] = typeof(bool);

            NewFn("instance?", xs => ((Type) xs[0]).IsInstanceOfType(xs[1]));

            NewFn("car", x => (x[0] as Cell)?.Head);
            NewFn("cdr", x => (x[0] as Cell)?.Tail);
            NewFn("cons", x => new Cell(x[0], (Seq) x[1]));

            NewFn("=", x => x[0].Equals(x[1]));
            NewFn("and", x => (bool) x[0] && (bool) x[1]);
            NewFn("or", x => (bool) x[0] || (bool) x[1]);
            NewFn("not", x => !(bool) x[0]);

            NewFn("+", x => (dynamic) x[0] + (dynamic) x[1]);
            NewFn("-", x => (dynamic) x[0] - (dynamic) x[1]);
            NewFn("*", x => (dynamic) x[0] * (dynamic) x[1]);
            NewFn("/", x => (dynamic) x[0] / (dynamic) x[1]);
            NewFn("<=", x => (dynamic) x[0] <= (dynamic) x[1]);
            NewFn(">=", x => (dynamic) x[0] >= (dynamic) x[1]);
            NewFn("<", x => (dynamic) x[0] < (dynamic) x[1]);
            NewFn(">", x => (dynamic) x[0] > (dynamic) x[1]);
            NewFn("concat", xs =>
            {
                return Seq.Create(xs.SelectMany(x => (Seq) x));
            });

            NewFn("repr", x => RailgunLibrary.Repr(x[0]));
            NewFn("print", x =>
            {
                Console.WriteLine(x[0]);
                return null;
            });
            NewFn("idx", x =>
            {
                var coll = (dynamic) x[0];
                var i = (dynamic) x[1];
                return coll[i];
            });

            NewFn("exit", x =>
            {
                Environment.Exit(0);
                return null;
            });
            NewFn("list", x => x.ToList());
            NewFn("dict", xs =>
            {
                if (xs.Length % 2 != 0)
                {
                    throw new RailgunRuntimeException("dict must have even number of values");
                }
                var dict = new Dictionary<object, object>();
                for (var i = 0; i < xs.Length; i+=2)
                {
                    dict[xs[i]] = xs[i + 1];
                }
                return dict;
            });
            NewFn("seq", Seq.Create);
            NewFn("foreach-fn", x =>
            {
                var list = (IEnumerable<object>) x[0];
                var f = (IRailgunClosure) x[1];
                foreach (var item in list)
                {
                    f.Eval(new Cell(item, Nil.Value));
                }
                return null;
            });

            NewFn("macroexpand", x => ExpandMacros(x[0], Globals));
            NewFn("decompile", xs => BytecodeCompiler.Decompile(
                ((Closure) xs[0]).Function.Body
            ));
            NewFn("str/fmt", x => string.Format((string) x[0], x.Skip(1).ToArray()));
            NewFn("|>", x => x.Skip(1).Aggregate(x[0], 
                (cx, fn) => ((IRailgunClosure) fn).Eval(new Cell(cx, Nil.Value))));

            NewFn(".", xs =>
            {
                var seed = xs[0];
                return xs.Skip(1).Aggregate(seed, (current, x) =>
                    ((IDottable) current).DotGet((string) x) );
            });
            
            NewFn("use", xs =>
            {
                var uenv = new RailgunEnvironment(Globals);
                var path = Path.Join(_workingDirectory, (string) xs[0]);
                var uProgram = ProgramLoader.LoadProgram(path);
                RunProgram(uProgram, uenv);
                return uenv;
            });
            
            NewMacro("quasiquote", x => Optimizer.LowerQuasiquotes(x[0], 1));
            // macros can be "values from above", that can't typically generated by the reader.
            NewMacro("quote", x => new QuoteExpr(x[0]));
            
            // externals
            NewFn("load-type", xs => Type.GetType((string) xs[0]));
            NewFn("invoke-ctor", xs => Activator.CreateInstance(
                (Type) xs[0],
                xs.Skip(1).ToArray()
            ));
            // TODO: Caching
            NewFn("invoke-method", xs =>
            {
                var mn = xs[0].GetType().GetMethod((string) xs[1]);
                return mn!.Invoke(xs[0], xs.Skip(2).ToArray());
            });
            NewFn("invoke-static-method", xs =>
            {
                var mn = ((Type) xs[0]).GetMethod((string) xs[1]);
                return mn!.Invoke(null, xs.Skip(2).ToArray());
            });
            
            RunProgram(new SweetParser(LoadEmbeddedFile("Railgun.core.core.rgx")).ParseSweetProgram());
        }

        private static bool TryGetMacro(object ex, IEnvironment env, out IRailgunClosure mac)
        {
            mac = null;
            switch (ex)
            {
                case IRailgunClosure{IsMacro: true} m:
                    mac = m;
                    return true;
                case NameExpr nex:
                {
                    var x = env[nex.Name];
                    if (x is IRailgunClosure{IsMacro: true} m2)
                    {
                        mac = m2;
                        return true;
                    }
                    break;
                }
            }
            return false;
        }

        private object ExpandMacros(object ex, IEnvironment env)
        {
            while (ex is Cell c && TryGetMacro(c.Head, env, out var mac))
            {
                ex = mac.Eval(c.Tail);
            }

            // expand the subexpressions after expanding the top cell
            if (ex is Cell cc)
            {
                return cc.Map(x => ExpandMacros(x, env));
            }
            return ex;
        }

        public object Eval(object ex, IEnvironment env = null, bool topLevel = false)
        {
            env ??= Globals;
            if (topLevel)
            {
                ex = ExpandMacros(ex, env);
                // Console.WriteLine(RailgunLibrary.Repr(ex));
            }

            var bc = new List<IByteCode>();
            BytecodeCompiler.CompileExpr(bc, ex);
            return BytecodeCompiler.ExecuteByteCode(bc, env);
        }
        
        public void RunProgram(IEnumerable<object> program, IEnvironment env = null)
        {
            env ??= Globals;
            foreach (var expr in program)
            {
                Eval(expr, env, topLevel: true);
            }
        }
    }
}