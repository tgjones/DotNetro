; ModuleID = './control-flow/nested-loop.c'
source_filename = "./control-flow/nested-loop.c"
target datalayout = "e-m:e-p:16:8-p1:8:8-i16:8-i32:8-i64:8-f32:8-f64:8-a:8-Fi8-n8"
target triple = "mos-unknown-unknown"

; Function Attrs: nofree norecurse nosync nounwind memory(none)
define dso_local i16 @nested_loop(i16 noundef %0, i16 noundef %1) local_unnamed_addr #0 {
  %3 = icmp sgt i16 %0, 0
  br i1 %3, label %4, label %21

4:                                                ; preds = %2
  %5 = icmp sgt i16 %1, 0
  %6 = add i16 %1, -1
  %7 = zext i16 %6 to i17
  %8 = add i16 %1, -2
  %9 = zext i16 %8 to i17
  %10 = mul i17 %7, %9
  %11 = lshr i17 %10, 1
  %12 = trunc nuw i17 %11 to i16
  %13 = add i16 %1, %12
  %14 = add i16 %13, -1
  br label %15

15:                                               ; preds = %4, %23
  %16 = phi i16 [ 0, %4 ], [ %26, %23 ]
  %17 = phi i16 [ 0, %4 ], [ %25, %23 ]
  %18 = phi i16 [ 0, %4 ], [ %24, %23 ]
  br i1 %5, label %19, label %23

19:                                               ; preds = %15
  %20 = add i16 %18, %16
  br label %23

21:                                               ; preds = %23, %2
  %22 = phi i16 [ 0, %2 ], [ %24, %23 ]
  ret i16 %22

23:                                               ; preds = %19, %15
  %24 = phi i16 [ %18, %15 ], [ %20, %19 ]
  %25 = add nuw nsw i16 %17, 1
  %26 = add i16 %16, %14
  %27 = icmp eq i16 %25, %0
  br i1 %27, label %21, label %15, !llvm.loop !6
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
