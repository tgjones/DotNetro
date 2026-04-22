// RUN: @cs_compiler @file | @dnrc --emit assembly

// CHECK: \.cstring "Hello, World!"
Console.WriteLine("Hello, World!");