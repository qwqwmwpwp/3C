using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace ThreeC.CharacterBehavior
{
    // 枚举编译后的纯指令；操作数始终为本输出内更早的寄存器。
    internal enum DecisionOp { Literal, Input, Named, Not, Add, Subtract, Equal, Different, Less, AtMost, Greater, AtLeast, And, Or, Select }

    // 保存一条不可变指令及其诊断名。
    internal readonly struct DecisionInstruction
    {
        internal readonly DecisionOp Op;
        internal readonly DecisionType Type;
        internal readonly int A, B, C;
        internal readonly float Value;
        internal readonly string Name;

        // 固定指令的输入索引、字面值与结果类型。
        internal DecisionInstruction(DecisionOp op, DecisionType type, string name, int a = 0, int b = 0, int c = 0, float value = 0)
        {
            Op = op;
            Type = type;
            Name = name;
            A = a;
            B = b;
            C = c;
            Value = value;
        }

        // 只读角色输入和已计算的寄存器，返回本条指令的值。
        internal float Read(float[] inputs, float[] registers)
        {
            switch (Op)
            {
                case DecisionOp.Literal: return Value;
                case DecisionOp.Input: return inputs[A];
                case DecisionOp.Named: return registers[A];
                case DecisionOp.Not: return registers[A] == 0 ? 1 : 0;
                case DecisionOp.Add: return registers[A] + registers[B];
                case DecisionOp.Subtract: return registers[A] - registers[B];
                case DecisionOp.Equal: return registers[A] == registers[B] ? 1 : 0;
                case DecisionOp.Different: return registers[A] != registers[B] ? 1 : 0;
                case DecisionOp.Less: return registers[A] < registers[B] ? 1 : 0;
                case DecisionOp.AtMost: return registers[A] <= registers[B] ? 1 : 0;
                case DecisionOp.Greater: return registers[A] > registers[B] ? 1 : 0;
                case DecisionOp.AtLeast: return registers[A] >= registers[B] ? 1 : 0;
                case DecisionOp.And: return registers[A] != 0 && registers[B] != 0 ? 1 : 0;
                case DecisionOp.Or: return registers[A] != 0 || registers[B] != 0 ? 1 : 0;
                case DecisionOp.Select: return registers[A] != 0 ? registers[B] : registers[C];
                default: throw new InvalidOperationException();
            }
        }
    }

    // 保存单项输出的指令、分支入口与按需诊断信息。
    internal sealed class DecisionOutputCode
    {
        internal readonly AnimationDecisionStage Stage;
        private readonly DecisionInstruction[] code;
        private readonly (int when, int value, string name)[] branches;
        internal int RegisterCount => code.Length;

        // 固定分支优先级和拓扑排序后的寄存器依赖。
        internal DecisionOutputCode(AnimationDecisionStage stage, DecisionInstruction[] code, (int, int, string)[] branches)
        {
            Stage = stage;
            this.code = code;
            this.branches = branches;
        }

        // 在调用方独占的寄存器中求值，选择第一个满足条件的分支。
        internal float Evaluate(float[] inputs, float[] registers, out int selected)
        {
            for (int i = 0; i < code.Length; i++) registers[i] = code[i].Read(inputs, registers);
            for (int i = 0; i < branches.Length; i++)
            {
                var branch = branches[i];
                if (branch.when >= 0 && registers[branch.when] == 0) continue;
                selected = i;
                return registers[branch.value];
            }
            throw new InvalidOperationException("已编译的输出缺少兜底分支。");
        }

#if UNITY_EDITOR
        // 展示本次求值读取的输入、命名节点、命中分支及其结果。
        internal void Describe(StringBuilder text, float[] registers, int selected)
        {
            var branch = branches[selected];
            text.Append(branch.name).Append(" → ").Append(registers[branch.value]).AppendLine();
            for (int i = 0; i < code.Length; i++)
                if (code[i].Op == DecisionOp.Named || code[i].Op == DecisionOp.Input)
                    text.Append("  ").Append(code[i].Name).Append(" = ").Append(registers[i]).AppendLine();
        }
#endif
    }

    // 在主线程验证所有模块输出；运行时只保留不含资产引用的索引指令。
    internal sealed class CompiledAnimationDecisions
    {
        internal static readonly int InputCount = Enum.GetValues(typeof(AnimationDecisionInput)).Length;
        internal static readonly int OutputCount = Enum.GetValues(typeof(AnimationDecisionOutput)).Length;
        private readonly DecisionOutputCode[] outputs = new DecisionOutputCode[OutputCount];
        internal DecisionOutputCode this[AnimationDecisionOutput output] => outputs[(int)output];

        // 检查命名冲突、循环、阶段依赖、分支类型以及完整兜底后一次性生成快照。
        internal CompiledAnimationDecisions(AnimationDecisionProgram.NamedExpression[] nodes, AnimationDecisionProgram.OutputBranch[] branches)
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var node in nodes ?? Array.Empty<AnimationDecisionProgram.NamedExpression>())
            {
                if (node == null || !DecisionExpressionParser.IsNodeName(node.Name) || names.ContainsKey(node.Name))
                    throw new ArgumentException($"无效、保留或重复的节点名 {node?.Name}。");
                names.Add(node.Name, node.Expression);
            }
            var used = new HashSet<string>();
            var rows = branches ?? Array.Empty<AnimationDecisionProgram.OutputBranch>();
            foreach (var row in rows)
                if (row == null || !Enum.IsDefined(typeof(AnimationDecisionOutput), row.Output))
                    throw new ArgumentException("输出分支为空或指定了无效输出。");
            for (int i = 0; i < outputs.Length; i++)
            {
                var output = (AnimationDecisionOutput)i;
                var slot = Slot(output);
                var compiler = new DecisionExpressionCompiler(names, used, slot.Stages);
                var choices = new List<(int, int, string)>();
                bool fallback = false;
                foreach (var row in rows)
                {
                    if (row.Output != output) continue;
                    if (fallback) throw new ArgumentException($"{output} 的兜底分支后仍有分支。");
                    fallback = string.IsNullOrWhiteSpace(row.When);
                    try
                    {
                        int when = fallback ? -1 : compiler.Parse(row.When, DecisionType.Flag);
                        int value = compiler.Parse(row.Value, slot.Type);
                        choices.Add((when, value, string.IsNullOrWhiteSpace(row.Name) ? (choices.Count + 1).ToString() : row.Name));
                    }
                    catch (ArgumentException error)
                    {
                        throw new ArgumentException($"{output} 分支 {row.Name}：{error.Message}", error);
                    }
                }
                if (!fallback) throw new ArgumentException($"{output} 缺少无条件兜底值。");
                outputs[i] = new DecisionOutputCode(slot.Stages, compiler.Freeze(), choices.ToArray());
            }
            foreach (var name in names.Keys)
                if (!used.Contains(name)) throw new ArgumentException($"节点 {name} 未被任何输出使用。");
        }

        // 只在装配阶段读取输入、输出字段标注的类型和适用阶段。
        internal static DecisionSlotAttribute Slot<T>(T field) where T : Enum =>
            typeof(T).GetField(field.ToString()).GetCustomAttribute<DecisionSlotAttribute>();
    }

    // 为一个角色的一个消费入口保存独占的黑板和寄存器，求值不会修改实例、计时或播放。
    internal sealed class AnimationDecisionEvaluator
    {
        private readonly CompiledAnimationDecisions program;
        private readonly float[] inputs = new float[CompiledAnimationDecisions.InputCount];
        private readonly float[][] registers = new float[CompiledAnimationDecisions.OutputCount][];
#if UNITY_EDITOR
        private readonly int[] selections = new int[CompiledAnimationDecisions.OutputCount];
#endif
        private AnimationDecisionStage stage;

        // 仅在配置安装时分配工作区，各输出保留自己最近一次求值的观测。
        internal AnimationDecisionEvaluator(CompiledAnimationDecisions program, AnimationDecisionStage stages)
        {
            this.program = program;
            for (int i = 0; i < registers.Length; i++)
            {
                var code = program[(AnimationDecisionOutput)i];
                if ((code.Stage & stages) != 0) registers[i] = new float[code.RegisterCount];
            }
            ClearTrace();
        }

        // 开始一次独立观测，避免模型在下一次采样看到上个阶段的输入。
        internal void Begin(AnimationDecisionStage next)
        {
            stage = next;
            Array.Clear(inputs, 0, inputs.Length);
        }

        // 写入本阶段的数值输入，可保留搜索代价中的正无穷。
        internal void Set(AnimationDecisionInput input, float value) => inputs[(int)input] = value;
        // 写入本阶段的条件输入。
        internal void Set(AnimationDecisionInput input, bool value) => inputs[(int)input] = value ? 1 : 0;
        // 写入本阶段的步态输入。
        internal void Set(AnimationDecisionInput input, DecisionGait value) => inputs[(int)input] = (int)value;

        // 按当前阶段读取指定输出，不提交角色状态。
        internal float Number(AnimationDecisionOutput output)
        {
            int i = (int)output;
            var code = program[output];
            if (code.Stage != stage || registers[i] == null)
                throw new InvalidOperationException($"当前工作区不能在 {stage} 求值 {output}（所属阶段 {code.Stage}）。");
#if UNITY_EDITOR
            return code.Evaluate(inputs, registers[i], out selections[i]);
#else
            return code.Evaluate(inputs, registers[i], out _);
#endif
        }

        // 将条件输出转换为布尔值。
        internal bool Flag(AnimationDecisionOutput output) => Number(output) != 0;

        // 清除旧批次的分支记录，使未执行的输出可以明确区分。
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        internal void ClearTrace()
        {
#if UNITY_EDITOR
            for (int i = 0; i < selections.Length; i++) selections[i] = -1;
#endif
        }

#if UNITY_EDITOR
        // 按需格式化本角色最近已求值的阶段、分支和节点，无需求值时分配文本。
        internal void AppendTrace(StringBuilder text)
        {
            for (int i = 0; i < selections.Length; i++)
            {
                if (registers[i] == null) continue;
                var output = (AnimationDecisionOutput)i;
                var code = program[output];
                text.Append(code.Stage).Append('/').Append(output).Append("  ");
                if (selections[i] < 0) text.AppendLine("未执行");
                else code.Describe(text, registers[i], selections[i]);
            }
        }
#endif
    }
}
