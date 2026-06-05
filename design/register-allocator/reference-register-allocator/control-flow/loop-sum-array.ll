; ModuleID = './control-flow/loop-sum-array.c'
source_filename = "./control-flow/loop-sum-array.c"
target datalayout = "e-m:e-p:16:8-p1:8:8-i16:8-i32:8-i64:8-f32:8-f64:8-a:8-Fi8-n8"
target triple = "mos-unknown-unknown"

; Function Attrs: nofree norecurse nosync nounwind memory(argmem: read)
define dso_local i16 @loop_sum_array(ptr noundef readonly captures(none) %0, i16 noundef %1) local_unnamed_addr #0 {
  %3 = icmp sgt i16 %1, 0
  br i1 %3, label %6, label %4

4:                                                ; preds = %6, %2
  %5 = phi i16 [ 0, %2 ], [ %11, %6 ]
  ret i16 %5

6:                                                ; preds = %2, %6
  %7 = phi i16 [ %12, %6 ], [ 0, %2 ]
  %8 = phi i16 [ %11, %6 ], [ 0, %2 ]
  %9 = getelementptr inbounds nuw [2 x i8], ptr %0, i16 %7
  %10 = load i16, ptr %9, align 1, !tbaa !2
  %11 = add nsw i16 %10, %8
  %12 = add nuw nsw i16 %7, 1
  %13 = icmp eq i16 %12, %1
  br i1 %13, label %4, label %6, !llvm.loop !6
}

attributes #0 = { nofree norecurse nosync nounwind memory(argmem: read) "frame-pointer"="all" "no-trapping-math"="true" "stack-protector-buffer-size"="8" }

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
