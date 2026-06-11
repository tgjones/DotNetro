; ModuleID = './memory/two-pointer-copy.c'
source_filename = "./memory/two-pointer-copy.c"
target datalayout = "e-m:e-p:16:8-p1:8:8-i16:8-i32:8-i64:8-f32:8-f64:8-a:8-Fi8-n8"
target triple = "mos-unknown-unknown"

; Function Attrs: nofree norecurse nosync nounwind memory(argmem: readwrite)
define dso_local void @two_pointer_copy(ptr noundef writeonly captures(none) %0, ptr noundef readonly captures(none) %1, i16 noundef %2) local_unnamed_addr #0 {
  %4 = icmp sgt i16 %2, 0
  br i1 %4, label %6, label %5

5:                                                ; preds = %6, %3
  ret void

6:                                                ; preds = %3, %6
  %7 = phi i16 [ %11, %6 ], [ 0, %3 ]
  %8 = getelementptr inbounds nuw [2 x i8], ptr %1, i16 %7
  %9 = load i16, ptr %8, align 1, !tbaa !2
  %10 = getelementptr inbounds nuw [2 x i8], ptr %0, i16 %7
  store i16 %9, ptr %10, align 1, !tbaa !2
  %11 = add nuw nsw i16 %7, 1
  %12 = icmp eq i16 %11, %2
  br i1 %12, label %5, label %6, !llvm.loop !6
}

attributes #0 = { nofree norecurse nosync nounwind memory(argmem: readwrite) "frame-pointer"="all" "no-trapping-math"="true" "stack-protector-buffer-size"="8" }

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
