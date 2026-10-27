// Shipped with A·R·I: the second half of Memory recall. It reads the notes the "Memory Pick" agent chose, has the "Memory Extract"
// agent pull the relevant lines out of each (one call per note, in parallel), and puts the memory block together. In a guarded
// conversation it also has the "Memory Privacy" agent check the block. State comes from Memory_Find.
public class Memory_Finish : ScriptNode
{
    public override string Description => "Turns the model's picks into the finished memory block.";
    public override int TimeoutSeconds => 600;   // one model call per note

    [Input]                   public string State { get; set; }
    [Input(Optional = true)]  public string Picks { get; set; }
    [Output]                  public string Block { get; set; }

    public override async Task Run(ScriptContext ctx)
    {
        if (ctx.Memory is null || State is null) return;
        Block = await ctx.Memory.Finish(State, Picks, ctx.Ct);
    }
}
