using System.Diagnostics;

/// <summary>
/// UI 用例必须串行：它们操作真实桌面窗口，并行时「同一互斥体的第二个实例」与前台切换互相打架，
/// 失败只会表现成随机的窗口找不到。
/// </summary>
[assembly: CollectionBehavior(DisableTestParallelization = true)]
