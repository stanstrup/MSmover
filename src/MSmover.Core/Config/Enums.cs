namespace MSmover.Core.Config;

public enum TransferMode
{
    /// <summary>Source is never deleted.</summary>
    Copy,
    /// <summary>Source is deleted only after the destination has been byte-verified.</summary>
    Move
}

public enum QueueOrder
{
    /// <summary>Most recently written file first (the default).</summary>
    NewestFirst,
    OldestFirst
}

public enum VerifyMode
{
    /// <summary>Read the destination back over the network and compare hashes. The default.</summary>
    Hash,
    /// <summary>Compare byte length only. Faster, will not catch silent corruption.</summary>
    Size,
    /// <summary>No verification. Refused in Move mode.</summary>
    None
}

public enum HashKind
{
    /// <summary>Non-cryptographic, several GB/s. Correct choice for integrity checking.</summary>
    XxHash64,
    Sha256,
    Md5
}

public enum OnTargetExists
{
    /// <summary>Log a warning, leave the source alone, do nothing.</summary>
    Skip
}

/// <summary>Which timestamp the {yyyy}/{MM}/{dd} template tokens read from.</summary>
public enum DateTokenSource
{
    FileModified,
    Now
}
