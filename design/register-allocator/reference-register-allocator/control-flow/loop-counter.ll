; ModuleID = './control-flow/loop-counter.c'
source_filename = "./control-flow/loop-counter.c"
target datalayout = "e-m:e-p:16:8-p1:8:8-i16:8-i32:8-i64:8-f32:8-f64:8-a:8-Fi8-n8"
target triple = "mos-unknown-unknown"

; Function Attrs: mustprogress nofree norecurse nosync nounwind willreturn memory(none)
define dso_local i16 @loop_counter(i16 noundef %0) local_unnamed_addr #0 {
  %2 = icmp sgt i16 %0, 0
  br i1 %2, label %3, label %13

3:                                                ; preds = %1
  %4 = add nsw i16 %0, -1
  %5 = zext nneg i16 %4 to i17
  %6 = add nsw i16 %0, -2
  %7 = zext i16 %6 to i17
  %8 = mul i17 %5, %7
  %9 = lshr i17 %8, 1
  %10 = trunc nuw i17 %9 to i16
  %11 = add i16 %0, %10
  %12 = add i16 %11, -1
  br label %13

13:                                               ; preds = %3, %1
  %14 = phi i16 [ 0, %1 ], [ %12, %3 ]
  ret i16 %14
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
