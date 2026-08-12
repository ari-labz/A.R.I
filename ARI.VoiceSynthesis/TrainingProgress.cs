namespace ARI.VoiceSynthesis;

public record TrainingProgress(string Step, int Percent, string? Detail = null);

public record TrainingSettings(
    string AudioPath,
    string ModelName,
    int Epochs = 100,
    int SaveEveryNEpochs = 10,
    Dictionary<string, string>? Transcripts = null);
