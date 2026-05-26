namespace Irie.Mir;

// Notified about instruction insertions/erasures performed via MirBuilder.
// Used by the legalizer to keep its worklists in sync as combines mutate
// the IR.
public interface IMirObserver
{
    void OnInstructionCreated(MirInstruction instruction);
    void OnInstructionErased(MirInstruction instruction);
}
