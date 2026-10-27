// Shipped with A·R·I: the starting point for the Memory walker. It works out who is talking, which notes the message points at, and
// who is listening, and hands that over as plain text. Starting is left empty when this conversation has no memory access, which
// makes the walker skip itself. It is an ordinary script: edit it here.
public class Memory_Start : ScriptNode
{
    public override string Description => "Works out where the Memory walker should start: the speaker, the notes the message points at, and who is listening.";

    [Input]  public string Message  { get; set; }
    [Output] public string Starting { get; set; }
    [Output] public string Audience { get; set; }

    public override Task Run(ScriptContext ctx)
    {
        if (ctx.Memory is null) return Task.CompletedTask;
        StartingPoints start = ctx.Memory.Start(Message);
        Starting = start.Text;
        Audience = start.Audience;
        return Task.CompletedTask;
    }
}
