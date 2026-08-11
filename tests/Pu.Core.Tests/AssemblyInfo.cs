using Xunit;

// 服务测试要占端口、集成测试要跑 ffmpeg —— 串行执行避免互相干扰
[assembly: CollectionBehavior(DisableTestParallelization = true)]
