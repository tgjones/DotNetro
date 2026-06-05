; ModuleID = './widths/mixed-widths.c'
source_filename = "./widths/mixed-widths.c"
target datalayout = "e-m:e-p:16:8-p1:8:8-i16:8-i32:8-i64:8-f32:8-f64:8-a:8-Fi8-n8"
target triple = "mos-unknown-unknown"

; Function Attrs: mustprogress nofree norecurse nosync nounwind willreturn memory(none)
define dso_local i32 @mixed_widths(i8 noundef zeroext %0, i16 noundef %1, i32 noundef %2) local_unnamed_addr #0 {
  %4 = zext i8 %0 to i16
  %5 = add nsw i16 %1, %4
  %6 = sext i16 %5 to i32
  %7 = zext i8 %0 to i32
  %8 = add i32 %2, %7
  %9 = add i32 %8, %6
  ret i32 %9
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
