// Shipped with A·R·I: the first half of Memory recall, as a pipeline node. It is an ordinary script, so you can edit it here.
// It searches the brain for the latest message and prepares what the "Memory Pick" agent is asked to choose from.
// Transcript and Candidates are left empty when nothing matched, which makes the pick step skip itself.
public class Memory_Find : ScriptNode
{
    public override string Description => "Searches the brain for the latest message and prepares the candidates for the model to choose from.";

    [Input]  public string Message    { get; set; }
    [Output] public string Transcript { get; set; }
    [Output] public string Candidates { get; set; }
    [Output] public string State      { get; set; }

    public override Task Run(ScriptContext ctx)
    {
        if (ctx.Memory is null) return Task.CompletedTask;   // no brain: every output stays empty
        MemoryFound found = ctx.Memory.Find(Message);
        State = found.State;
        Transcript = found.Transcript;
        Candidates = found.Candidates;
        return Task.CompletedTask;
    }
}
