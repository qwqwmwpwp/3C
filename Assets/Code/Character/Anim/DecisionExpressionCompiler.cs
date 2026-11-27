using System;
using System.Collections.Generic;
using System.Globalization;

namespace ThreeC.CharacterBehavior
{
    // 将某项输出的所有表达式编译到同一组寄存器，跨分支复用命名节点与输入。
    internal sealed class DecisionExpressionCompiler
    {
        private readonly Dictionary<string, string> definitions;
        private readonly HashSet<string> used;
        private readonly AnimationDecisionStage stage;
        private readonly List<DecisionInstruction> code = new List<DecisionInstruction>();
        private readonly Dictionary<string, int> symbols = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> visiting = new HashSet<string>();
        private int parseDepth;

        // 在同一输出的所有解析器间共用递归预算，命名依赖不能重新开始计数。
        internal void EnterExpression()
        {
            if (++parseDepth > 128) throw new ArgumentException("表达式和命名依赖的总嵌套超过 128 层。");
        }

        // 返回上层表达式时释放本层预算。
        internal void LeaveExpression() => parseDepth--;

        // 绑定同一资产的节点定义和当前输出可以读取的观测阶段。
        internal DecisionExpressionCompiler(Dictionary<string, string> definitions, HashSet<string> used, AnimationDecisionStage stage)
        {
            this.definitions = definitions;
            this.used = used;
            this.stage = stage;
        }

        // 返回不可变指令列表。
        internal DecisionInstruction[] Freeze() => code.ToArray();

        // 检查整个表达式的输出类型并返回根寄存器。
        internal int Parse(string expression, DecisionType expected)
        {
            int index = new DecisionExpressionParser(expression, this).Parse();
            Expect(index, expected);
            return index;
        }

        // 在依赖之后追加一条指令。
        private int Add(DecisionInstruction instruction)
        {
            code.Add(instruction);
            return code.Count - 1;
        }

        // 检查操作数与声明的类型一致。
        private void Expect(int index, DecisionType expected)
        {
            if (code[index].Type != expected)
                throw new ArgumentException($"{code[index].Name} 需要 {expected}，实际为 {code[index].Type}。");
        }

        // 生成给定类型的常量。
        internal int Literal(float value, DecisionType type, string name) =>
            Add(new DecisionInstruction(DecisionOp.Literal, type, name, value: value));

        // 解析输入、有名依赖或枚举常量，并拒绝循环及无效的阶段引用。
        internal int Symbol(string name)
        {
            if (symbols.TryGetValue(name, out int index)) return index;
            if (name == "true" || name == "false") index = Literal(name == "true" ? 1 : 0, DecisionType.Flag, name);
            else if (Enum.TryParse(name, out AnimationDecisionInput input) && Enum.IsDefined(typeof(AnimationDecisionInput), input))
            {
                var slot = CompiledAnimationDecisions.Slot(input);
                if ((slot.Stages & stage) == 0) throw new ArgumentException($"{stage} 无法读取 {name}。");
                index = Add(new DecisionInstruction(DecisionOp.Input, slot.Type, name, (int)input));
            }
            else if (definitions.TryGetValue(name, out var expression))
            {
                if (!visiting.Add(name)) throw new ArgumentException($"节点 {name} 存在循环依赖。");
                int dependency = new DecisionExpressionParser(expression, this).Parse();
                visiting.Remove(name);
                used.Add(name);
                index = Add(new DecisionInstruction(DecisionOp.Named, code[dependency].Type, name, dependency));
            }
            else index = EnumLiteral(name);
            symbols.Add(name, index);
            return index;
        }

        // 校验领域常量名称及取值并保留其类型。
        private int EnumLiteral(string name)
        {
            string[] parts = name.Split('.');
            if (parts.Length == 2)
            {
                if (parts[0] == "Gait" && Enum.IsDefined(typeof(DecisionGait), parts[1]))
                    return Literal((int)Enum.Parse(typeof(DecisionGait), parts[1]), DecisionType.Gait, name);
                if (parts[0] == "Stride" && Enum.IsDefined(typeof(DecisionStride), parts[1]))
                    return Literal((int)Enum.Parse(typeof(DecisionStride), parts[1]), DecisionType.Stride, name);
                if (parts[0] == "Release" && Enum.IsDefined(typeof(DecisionRelease), parts[1]))
                    return Literal((int)Enum.Parse(typeof(DecisionRelease), parts[1]), DecisionType.Release, name);
            }
            throw new ArgumentException($"未知的输入、节点或常量 {name}。");
        }

        // 检查比较、逻辑或数值运算所需的操作数并生成指令。
        internal int Operation(DecisionOp op, int a, int b = 0, int c = 0)
        {
            DecisionType type = DecisionType.Flag;
            switch (op)
            {
                case DecisionOp.Not: Expect(a, DecisionType.Flag); break;
                case DecisionOp.And:
                case DecisionOp.Or: Expect(a, DecisionType.Flag); Expect(b, DecisionType.Flag); break;
                case DecisionOp.Select:
                    Expect(a, DecisionType.Flag);
                    type = code[b].Type;
                    Expect(c, type);
                    break;
                case DecisionOp.Equal:
                case DecisionOp.Different: Expect(b, code[a].Type); break;
                default:
                    Expect(a, DecisionType.Number);
                    Expect(b, DecisionType.Number);
                    if (op == DecisionOp.Add || op == DecisionOp.Subtract) type = DecisionType.Number;
                    break;
            }
            return Add(new DecisionInstruction(op, type, op.ToString(), a, b, c));
        }
    }

    // 在主线程解析括号、选择、逻辑和四种有序比较，运行时不读取表达式文本。
    internal sealed class DecisionExpressionParser
    {
        private readonly string source;
        private readonly DecisionExpressionCompiler compiler;
        private int cursor;
        private string token;

        // 初始化游标并读取第一个词。
        internal DecisionExpressionParser(string source, DecisionExpressionCompiler compiler)
        {
            this.source = source ?? string.Empty;
            this.compiler = compiler;
            Next();
        }

        // 命名节点只接受标识符并避开所有内建输入和关键字。
        internal static bool IsNodeName(string name)
        {
            if (string.IsNullOrEmpty(name) || !(char.IsLetter(name[0]) || name[0] == '_') ||
                name == "true" || name == "false" || name == "Select" || Enum.IsDefined(typeof(AnimationDecisionInput), name))
                return false;
            foreach (char c in name) if (!char.IsLetterOrDigit(c) && c != '_') return false;
            return true;
        }

        // 读取整条表达式并拒绝末尾多余的词。
        internal int Parse()
        {
            int root = Expression(1);
            if (token.Length != 0) throw Error("多余的词 " + token);
            return root;
        }

        // 以运算符优先级建立左结合依赖。
        private int Expression(int minimum)
        {
            compiler.EnterExpression();
            try
            {
                int left = Atom();
                while (Binary(token, out var op, out int precedence) && precedence >= minimum)
                {
                    Next();
                    left = compiler.Operation(op, left, Expression(precedence + 1));
                }
                return left;
            }
            finally { compiler.LeaveExpression(); }
        }

        // 解析数值、引用、括号、前缀运算符和三元选择。
        private int Atom()
        {
            string current = token;
            if (current.Length == 0) throw Error("缺少值");
            Next();
            switch (current)
            {
                case "(": int group = Expression(1); Require(")"); return group;
                case "!": return compiler.Operation(DecisionOp.Not, Expression(6));
                case "-": return compiler.Operation(DecisionOp.Subtract, compiler.Literal(0, DecisionType.Number, "0"), Expression(6));
                case "Select":
                    Require("(");
                    int condition = Expression(1);
                    Require(",");
                    int yes = Expression(1);
                    Require(",");
                    int no = Expression(1);
                    Require(")");
                    return compiler.Operation(DecisionOp.Select, condition, yes, no);
            }
            if (char.IsDigit(current[0]) || current[0] == '.')
            {
                if (float.TryParse(current, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) &&
                    !float.IsNaN(value) && !float.IsInfinity(value))
                    return compiler.Literal(value, DecisionType.Number, current);
                throw Error("无效数值 " + current);
            }
            if (char.IsLetter(current[0]) || current[0] == '_') return compiler.Symbol(current);
            throw Error("缺少操作数，遇到 " + current);
        }

        // 识别二元运算及其结合优先级。
        private static bool Binary(string text, out DecisionOp op, out int precedence)
        {
            (op, precedence) = text switch
            {
                "||" => (DecisionOp.Or, 1), "&&" => (DecisionOp.And, 2),
                "==" => (DecisionOp.Equal, 3), "!=" => (DecisionOp.Different, 3),
                "<" => (DecisionOp.Less, 4), "<=" => (DecisionOp.AtMost, 4),
                ">" => (DecisionOp.Greater, 4), ">=" => (DecisionOp.AtLeast, 4),
                "+" => (DecisionOp.Add, 5), "-" => (DecisionOp.Subtract, 5),
                _ => (DecisionOp.Literal, 0)
            };
            return precedence != 0;
        }

        // 校验并消耗标点。
        private void Require(string expected)
        {
            if (token != expected) throw Error("应为 " + expected);
            Next();
        }

        // 读取下一个标识符、十进制数、标点或操作符。
        private void Next()
        {
            while (cursor < source.Length && char.IsWhiteSpace(source[cursor])) cursor++;
            int start = cursor;
            if (cursor == source.Length) { token = string.Empty; return; }
            char first = source[cursor++];
            if (char.IsLetterOrDigit(first) || first == '_' || first == '.')
            {
                while (cursor < source.Length && (char.IsLetterOrDigit(source[cursor]) ||
                    source[cursor] == '_' || source[cursor] == '.')) cursor++;
            }
            else if (cursor < source.Length && ((source[cursor] == '=' && "<>!=".IndexOf(first) >= 0) ||
                (first == '&' || first == '|') && source[cursor] == first)) cursor++;
            token = source.Substring(start, cursor - start);
        }

        // 返回具有原表达式及读取位置的装配错误。
        private ArgumentException Error(string message) => new ArgumentException($"{message}（位置 {cursor}，表达式 {source}）。");
    }
}
