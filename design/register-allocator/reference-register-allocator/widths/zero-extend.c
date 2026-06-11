// Zero-extend a char to int: the high byte must be set to 0, which the
// allocator materializes in a register or reuses a known-zero value. Tests how
// narrow live-ins are widened into wider live ranges.
int zero_extend(unsigned char a) {
    return (int)a + 1;
}
