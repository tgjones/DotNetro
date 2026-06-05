; ModuleID = './realistic/gcd.c'
source_filename = "./realistic/gcd.c"
target datalayout = "e-m:e-p:16:8-p1:8:8-i16:8-i32:8-i64:8-f32:8-f64:8-a:8-Fi8-n8"
target triple = "mos-unknown-unknown"

; Function Attrs: nofree norecurse nosync nounwind memory(none)
define dso_local i16 @gcd(i16 noundef %0, i16 noundef %1) local_unnamed_addr #0 {
  %3 = icmp eq i16 %1, 0
  br i1 %3, label %9, label %4

4:                                                ; preds = %2, %4
  %5 = phi i16 [ %6, %4 ], [ %0, %2 ]
  %6 = phi i16 [ %7, %4 ], [ %1, %2 ]
  %7 = srem i16 %5, %6
  %8 = icmp eq i16 %7, 0
  br i1 %8, label %9, label %4, !llvm.loop !6

9:                                                ; preds = %4, %2
  %10 = phi i16 [ %0, %2 ], [ %6, %4 ]
  ret i16 %10
}

attributes #0 = { nofree norecurse nosync nounwind memory(none) "frame-pointer"="all" "no-trapping-math"="true" "stack-protector-buffer-size"="8" }

!llvm.module.flags = !{!0}
!llvm.ident = !{!1}
!llvm.errno.tbaa = !{!2}

!0 = !{i32 7, !"frame-pointer", i32 2}
!1 = !{!"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"}
!2 = !{!3, !3, i64 0}
!3 = !{!"int", !4, i64 0}
!4 = !{!"omnipotent char", !5, i64 0}
!5 = !{!"Simple C/C++ TBAA"}
!6 = distinct !{!6, !7}
!7 = !{!"llvm.loop.mustprogress"}
