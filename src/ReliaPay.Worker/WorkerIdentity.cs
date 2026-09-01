namespace ReliaPay.Worker;

/// <summary>
/// Идентичность worker в <c>api.invoke</c>: checker сверяет <c>action_dispatches.principal</c>.
/// </summary>
public static class WorkerIdentity
{
    /// <summary>Значение <c>principal</c> в доверенном контексте.</summary>
    public const string Principal = "workflow-worker";

    /// <summary>Значение <c>consumer</c> в доверенном контексте.</summary>
    public const string Consumer = "internal";
}
