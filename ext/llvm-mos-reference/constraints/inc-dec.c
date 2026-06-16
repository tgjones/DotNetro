// Increment/decrement can use INC/DEC (memory or zp) or INX/INY/DEX/DEY, which
// are tied to specific registers. Tests whether the allocator places values in
// $x/$y to enable the cheaper register-specific increment instructions.
char inc_dec(char a, char b) {
    a++;
    b--;
    return a + b;
}
