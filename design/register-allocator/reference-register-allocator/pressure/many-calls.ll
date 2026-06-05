; ModuleID = './pressure/many-calls.c'
source_filename = "./pressure/many-calls.c"
target datalayout = "e-m:e-p:16:8-p1:8:8-i16:8-i32:8-i64:8-f32:8-f64:8-a:8-Fi8-n8"
target triple = "mos-unknown-unknown"

; Function Attrs: nounwind
define dso_local i16 @many_calls(i16 noundef %0, i16 noundef %1, i16 noundef %2, i16 noundef %3) local_unnamed_addr #0 {
  %5 = tail call i16 @g(i16 noundef %0) #2
  %6 = tail call i16 @g(i16 noundef %1) #2
  %7 = tail call i16 @g(i16 noundef %2) #2
  %8 = tail call i16 @g(i16 noundef %3) #2
  %9 = add i16 %1, %0
  %10 = add i16 %9, %2
  %11 = add i16 %10, %3
  %12 = add i16 %11, %5
  %13 = add i16 %12, %6
  %14 = add i16 %13, %7
  %15 = add i16 %14, %8
  ret i16 %15
}

declare dso_local i16 @g(i16 noundef) local_unnamed_addr #1

attributes #0 = { nounwind "frame-pointer"="all" "no-trapping-math"="true" "stack-protector-buffer-size"="8" }
attributes #1 = { "frame-pointer"="all" "no-trapping-math"="true" "stack-protector-buffer-size"="8" }
attributes #2 = { nounwind }

!llvm.module.flags = !{!0}
!llvm.ident = !{!1}
!llvm.errno.tbaa = !{!2}

!0 = !{i32 7, !"frame-pointer", i32 2}
!1 = !{!"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"}
!2 = !{!3, !3, i64 0}
!3 = !{!"int", !4, i64 0}
!4 = !{!"omnipotent char", !5, i64 0}
!5 = !{!"Simple C/C++ TBAA"}
