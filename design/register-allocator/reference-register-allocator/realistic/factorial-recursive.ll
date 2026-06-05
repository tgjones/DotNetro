; ModuleID = './realistic/factorial-recursive.c'
source_filename = "./realistic/factorial-recursive.c"
target datalayout = "e-m:e-p:16:8-p1:8:8-i16:8-i32:8-i64:8-f32:8-f64:8-a:8-Fi8-n8"
target triple = "mos-unknown-unknown"

; Function Attrs: nofree norecurse nosync nounwind memory(none)
define dso_local range(i16 1, -32768) i16 @factorial(i16 noundef %0) local_unnamed_addr #0 {
  %2 = icmp slt i16 %0, 2
  br i1 %2, label %9, label %3

3:                                                ; preds = %1, %3
  %4 = phi i16 [ %6, %3 ], [ %0, %1 ]
  %5 = phi i16 [ %7, %3 ], [ 1, %1 ]
  %6 = add nsw i16 %4, -1
  %7 = mul nuw nsw i16 %4, %5
  %8 = icmp samesign ult i16 %4, 3
  br i1 %8, label %9, label %3

9:                                                ; preds = %3, %1
  %10 = phi i16 [ 1, %1 ], [ %7, %3 ]
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
