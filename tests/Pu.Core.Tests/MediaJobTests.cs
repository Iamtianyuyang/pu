using Pu.Core.Serving;
using Xunit;

namespace Pu.Core.Tests;

/// <summary>MediaJob 状态机：先可播、后补字幕的语义（直出/复用命中路径）。</summary>
public class MediaJobTests
{
    private static MediaJob NewJob() => new()
    {
        Token = "t1",
        SourcePath = "x.mkv",
        Title = "x",
        SourceDescription = "d",
        ArtifactPath = "out.mp4",
        ContentType = "video/mp4",
        PlanExplanation = "e",
    };

    [Fact]
    public void 初始状态_转码中且字幕未定案()
    {
        var job = NewJob();
        Assert.Equal(JobState.Transcoding, job.State);
        Assert.True(job.SubtitlesPending);
    }

    [Fact]
    public void SetServing无参_可播但字幕仍待补()
    {
        var job = NewJob();
        job.SetServing();
        Assert.Equal(JobState.Serving, job.State);
        Assert.True(job.SubtitlesPending);
        Assert.Empty(job.Subtitles);
    }

    [Fact]
    public void SetSubtitles_字幕定案_空表也算定案()
    {
        var job = NewJob();
        job.SetServing();
        job.SetSubtitles([]);
        Assert.False(job.SubtitlesPending);
        Assert.Empty(job.Subtitles);
    }

    [Fact]
    public void SetServing带字幕_一步定案()
    {
        var job = NewJob();
        job.SetServing([new SubtitleFile(2, "subrip", "chi", "", "2.vtt")]);
        Assert.False(job.SubtitlesPending);
        Assert.Single(job.Subtitles);
        Assert.Equal(2, job.Subtitles[0].StreamIndex);
    }

    [Fact]
    public void SetFailed_状态失败且字幕一并定案()
    {
        // 失败即定案：SubtitlesPending 归位，订阅者退订/状态页轮询不再续命
        var job = NewJob();
        job.SetFailed("boom");
        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("boom", job.Error);
        Assert.False(job.SubtitlesPending);
    }
}
