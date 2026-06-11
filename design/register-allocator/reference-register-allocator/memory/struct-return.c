// Returning a small struct by value spreads the result across several return
// registers. Tests how the allocator places multiple independent live-out
// values into their fixed return locations.
struct pair { int a; int b; };
struct pair struct_return(int x) {
    struct pair r;
    r.a = x + 1;
    r.b = x - 1;
    return r;
}
