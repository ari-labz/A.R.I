namespace ARI.VoiceSynthesis;

public interface IVoiceTrainer
{
    Task<string> Train(IProgress<TrainingProgress>? progress = null, CancellationToken ct = default);
}
