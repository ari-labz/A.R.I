namespace ARI.API;

public class APIConfig
{
    public bool   Enabled { get; init; }
    public int    Port    { get; init; } = 5000;
    public string LogPath { get; init; } = "";
}
