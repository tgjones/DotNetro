// A byte-oriented CRC8 over a buffer: a nested loop (per byte, per bit) with a
// loop-carried CRC accumulator, bit tests, shifts and a conditional XOR. Dense
// mix of constraints, control flow and pointer iteration -- a realistic
// embedded routine that pushes the allocator on every axis at once.
unsigned char crc8(const unsigned char *data, unsigned len) {
    unsigned char crc = 0xFF;
    for (unsigned i = 0; i < len; i++) {
        crc ^= data[i];
        for (int b = 0; b < 8; b++) {
            if (crc & 0x80) {
                crc = (unsigned char)((crc << 1) ^ 0x31);
            } else {
                crc = (unsigned char)(crc << 1);
            }
        }
    }
    return crc;
}
