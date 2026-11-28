#if UNITY_EDITOR
using System.Runtime.CompilerServices;

// 允许项目专属编辑器读取角色行为和预测的内部快照。
[assembly: InternalsVisibleTo("Assembly-CSharp-Editor")]
#endif
