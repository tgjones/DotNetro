namespace Irie.Passes;

// Thrown by GenericMirVerifierPass when generic-dialect MIR violates an
// invariant — e.g. an inline Immediate value literal in a generic operand slot
// that should be an arith.constant def.
public sealed class MirVerificationException(string message) : Exception(message);
