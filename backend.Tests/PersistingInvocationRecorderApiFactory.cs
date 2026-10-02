namespace backend.Tests;

/// <summary>
/// NivoraApiFactory with Program.cs's real AiInvocationRecorder left in
/// place (A1 Slice 4b design §7.3): rows are persisted to the dev DB. For
/// registration and persistence tests only — each user must delete its
/// AiProviderInvocations rows before its Businesses (Restrict FK).
/// </summary>
public sealed class PersistingInvocationRecorderApiFactory : NivoraApiFactory
{
    protected override bool UsesRealInvocationRecorder => true;
}
