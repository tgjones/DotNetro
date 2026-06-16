// Truncating an int to char keeps only the low byte; the high byte's live range
// ends early. Tests that the allocator frees the dead high-byte register
// promptly rather than carrying it to the return.
char truncate(int a, int b) {
    return (char)(a + b);
}
