; ModuleID = './basics/many-args.c'
source_filename = "./basics/many-args.c"
target datalayout = "e-m:e-p:16:8-p1:8:8-i16:8-i32:8-i64:8-f32:8-f64:8-a:8-Fi8-n8"
target triple = "mos-unknown-unknown"

; Function Attrs: mustprogress nofree norecurse nosync nounwind willreturn memory(none)
define dso_local i16 @many_args(i16 noundef %0, i16 noundef %1, i16 noundef %2, i16 noundef %3, i16 noundef %4, i16 noundef %5) local_unnamed_addr #0 {
  %7 = add nsw i16 %1, %0
  %8 = add nsw i16 %7, %2
  %9 = add nsw i16 %8, %3
  %10 = add nsw i16 %9, %4
  %11 = add nsw i16 %10, %5
  ret i16 %11
}

attributes #0 = { mustprogress nofree norecurse nosync nounwind willreturn memory(none) "frame-pointer"="all" "no-trapping-math"="true" "stack-protector-buffer-size"="8" }

!llvm.module.flags = !{!0}
!llvm.ident = !{!1}
!llvm.errno.tbaa = !{!2}

!0 = !{i32 7, !"frame-pointer", i32 2}
!1 = !{!"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"}
!2 = !{!3, !3, i64 0}
!3 = !{!"int", !4, i64 0}
!4 = !{!"omnipotent char", !5, i64 0}
!5 = !{!"Simple C/C++ TBAA"}
