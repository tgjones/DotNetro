; ModuleID = './realistic/fibonacci.c'
source_filename = "./realistic/fibonacci.c"
target datalayout = "e-m:e-p:16:8-p1:8:8-i16:8-i32:8-i64:8-f32:8-f64:8-a:8-Fi8-n8"
target triple = "mos-unknown-unknown"

; Function Attrs: nofree norecurse nosync nounwind memory(none)
define dso_local i16 @fibonacci(i16 noundef %0) local_unnamed_addr #0 {
  %2 = icmp sgt i16 %0, 0
  br i1 %2, label %5, label %3

3:                                                ; preds = %5, %1
  %4 = phi i16 [ 0, %1 ], [ %8, %5 ]
  ret i16 %4

5:                                                ; preds = %1, %5
  %6 = phi i16 [ %8, %5 ], [ 0, %1 ]
  %7 = phi i16 [ %10, %5 ], [ 0, %1 ]
  %8 = phi i16 [ %9, %5 ], [ 1, %1 ]
  %9 = add nsw i16 %6, %8
  %10 = add nuw nsw i16 %7, 1
  %11 = icmp eq i16 %10, %0
  br i1 %11, label %3, label %5, !llvm.loop !6
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
