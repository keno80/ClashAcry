using Xunit;

// 命名管道名固定（ContractConstants.PipeName），同进程两个 PipeServer 会互相撞车；
// Task 8 的注册表用例同理要求独占。整套串行是这个项目的既有代价，不是本任务的缺陷。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
