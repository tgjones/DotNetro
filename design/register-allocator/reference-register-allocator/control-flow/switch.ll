; ModuleID = './control-flow/switch.c'
source_filename = "./control-flow/switch.c"
target datalayout = "e-m:e-p:16:8-p1:8:8-i16:8-i32:8-i64:8-f32:8-f64:8-a:8-Fi8-n8"
target triple = "mos-unknown-unknown"

; Function Attrs: mustprogress nofree norecurse nosync nounwind willreturn memory(none)
define dso_local range(i16 -1, 41) i16 @switch_val(i16 noundef %0) local_unnamed_addr #0 {
  %2 = icmp ult i16 %0, 4
  br i1 %2, label %3, label %6

3:                                                ; preds = %1
  %4 = mul nuw nsw i16 %0, 10
  %5 = add nuw nsw i16 %4, 10
  br label %6

6:                                                ; preds = %1, %3
  %7 = phi i16 [ %5, %3 ], [ -1, %1 ]
  ret i16 %7
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
