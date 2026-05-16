/// <summary>
/// Identifies which constant-time bucket cascade stage made a decisive fraud-count decision.
/// </summary>
internal enum BucketFastPathStage : byte
{
    None = 0,
    Profile = 1,
    Reference1 = 2,
    Reference2 = 3
}
