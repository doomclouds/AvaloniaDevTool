using System.Text.Json;
using AvaloniaDevTool.Recording;
using Xunit;

namespace AvaloniaDevTool.Protocol.Tests;

public sealed class RecordingContractTests
{
    private static RecordingStartRequest Valid => new()
    {
        ProcessId = 123, StartTimeUtc = DateTimeOffset.UtcNow, WindowHandle = 456
    };

    [Fact]
    public void Default_directory_is_optional_but_an_explicit_directory_must_be_absolute()
    {
        RecordingSession.Validate(Valid);
        var error = Assert.Throws<RecordingException>(() =>
            RecordingSession.Validate(Valid with { OutputDirectory = "relative/videos" }));
        Assert.Equal("invalid_output_directory", error.Code);
    }

    [Theory]
    [InlineData(0, 0, 300, 200, true)]
    [InlineData(-1, 0, 300, 200, false)]
    [InlineData(0, 0, 0, 200, false)]
    [InlineData(0, 0, 100000, 100000, false)]
    public void Crop_contract_rejects_invalid_or_unbounded_regions(int x, int y, int width, int height, bool valid)
    {
        var request = Valid with { Crop = new RecordingCrop(x, y, width, height) };
        if (valid)
            RecordingSession.Validate(request);
        else
            Assert.Equal("invalid_recording_region", Assert.Throws<RecordingException>(() =>
                RecordingSession.Validate(request)).Code);
    }

    [Fact]
    public void Admission_rejects_a_second_owner_and_releases_after_disposal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "avdt-recording-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using (RecordingAdmission.Acquire(directory))
                Assert.Equal("recording_busy", Assert.Throws<RecordingException>(() =>
                    RecordingAdmission.Acquire(directory)).Code);
            using var next = RecordingAdmission.Acquire(directory);
        }
        finally
        {
            File.Delete(Path.Combine(directory, "active.lock"));
            Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData("../private")]
    [InlineData("recording-../../private")]
    [InlineData("recording-not-a-guid")]
    public void Artifact_lookup_only_accepts_opaque_recording_references(string reference) =>
        Assert.Equal("recording_not_found", Assert.Throws<RecordingException>(() => RecordingArtifacts.Get(reference)).Code);

    [Theory]
    [InlineData(0, 3000000, 120)]
    [InlineData(61, 3000000, 120)]
    [InlineData(30, 1, 120)]
    [InlineData(30, 3000000, 0)]
    public void Invalid_encoding_budgets_fail_before_start(int fps, int bitrate, int seconds) =>
        Assert.Equal("invalid_recording_options", Assert.Throws<RecordingException>(() =>
            RecordingSession.Validate(Valid with { FrameRate = fps, Bitrate = bitrate, MaxDurationSeconds = seconds })).Code);

    [Fact]
    public void Budget_minimum_matches_the_worker_streaming_finalization_allowance()
    {
        Assert.Equal("invalid_recording_options", Assert.Throws<RecordingException>(() =>
            RecordingSession.Validate(Valid with { MaxBytes = 1024 * 1024 })).Code);
        RecordingSession.Validate(Valid with { MaxBytes = 2 * 1024 * 1024 });
    }

    [Fact]
    public async Task Typed_marker_streaming_preserves_the_shared_worker_envelope()
    {
        using var stream = new MemoryStream();
        var marker = new RecordingInputMarker("PointerDown", 12.5, 67, "Left");
        await JsonSerializer.SerializeAsync(stream, new RecordingPipeCommand<RecordingInputMarker>(17, "marker", marker),
            RecordingJsonContext.Default.MarkerCommand);
        stream.Position = 0;
        var request = await JsonSerializer.DeserializeAsync(stream, RecordingJsonContext.Default.RecordingWorkerRequest);
        Assert.NotNull(request);
        Assert.Equal(17, request.Id);
        Assert.Equal("marker", request.Command);
        Assert.Equal(marker, request.Payload.Deserialize(RecordingJsonContext.Default.RecordingInputMarker));
    }

    [Fact]
    public void Typed_worker_response_keeps_memory_and_texture_pool_observations()
    {
        var status = new RecordingStatus
        {
            RecordingId = "recording-test", OutputPath = "test.mp4", State = "recording",
            ManagedMemoryObserved = true, ManagedHeapBytes = 12345, ManagedAllocatedBytes = 23456,
            LohBytesAfterLastCollection = 0, LastGcIndex = 3,
            TexturePoolObserved = true, EncoderTexturePoolCapacity = 8, EncoderTexturePoolAllocated = 4,
            EncoderTexturePoolInUse = 2, EncoderTexturePoolPeakInUse = 3,
            EncoderTexturePoolReuses = 99, EncoderTexturePoolBytes = 45678
        };
        var json = JsonSerializer.Serialize(new RecordingWorkerResponse(29, true,
                JsonSerializer.SerializeToElement(status, RecordingJsonContext.Default.RecordingStatus)),
            RecordingJsonContext.Default.RecordingWorkerResponse);
        var response = JsonSerializer.Deserialize(json, RecordingJsonContext.Default.RecordingPipeResponse);
        Assert.NotNull(response);
        Assert.Equal(29, response.Id);
        Assert.True(response.Success);
        Assert.NotNull(response.Result);
        Assert.Equal(JsonSerializer.Serialize(status, RecordingJsonContext.Default.RecordingStatus),
            JsonSerializer.Serialize(response.Result, RecordingJsonContext.Default.RecordingStatus));
    }
}
