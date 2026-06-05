; ModuleID = './memory/struct-return-sret.c'
source_filename = "./memory/struct-return-sret.c"
target datalayout = "e-m:e-p:16:8-p1:8:8-i16:8-i32:8-i64:8-f32:8-f64:8-a:8-Fi8-n8"
target triple = "mos-unknown-unknown"

%struct.big = type { i16, i16, i16, i16, i16 }

; Function Attrs: mustprogress nofree norecurse nosync nounwind willreturn memory(argmem: write)
define dso_local void @make_big(ptr dead_on_unwind noalias writable writeonly sret(%struct.big) align 1 captures(none) initializes((0, 10)) %0, i16 noundef %1) local_unnamed_addr #0 {
  store i16 %1, ptr %0, align 1, !tbaa !6
  %3 = getelementptr inbounds nuw i8, ptr %0, i16 2
  %4 = add nsw i16 %1, 1
  store i16 %4, ptr %3, align 1, !tbaa !8
  %5 = getelementptr inbounds nuw i8, ptr %0, i16 4
  %6 = add nsw i16 %1, 2
  store i16 %6, ptr %5, align 1, !tbaa !9
  %7 = getelementptr inbounds nuw i8, ptr %0, i16 6
  %8 = add nsw i16 %1, 3
  store i16 %8, ptr %7, align 1, !tbaa !10
  %9 = getelementptr inbounds nuw i8, ptr %0, i16 8
  %10 = add nsw i16 %1, 4
  store i16 %10, ptr %9, align 1, !tbaa !11
  ret void
}

attributes #0 = { mustprogress nofree norecurse nosync nounwind willreturn memory(argmem: write) "frame-pointer"="all" "no-trapping-math"="true" "stack-protector-buffer-size"="8" }

!llvm.module.flags = !{!0}
!llvm.ident = !{!1}
!llvm.errno.tbaa = !{!2}

!0 = !{i32 7, !"frame-pointer", i32 2}
!1 = !{!"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"}
!2 = !{!3, !3, i64 0}
!3 = !{!"int", !4, i64 0}
!4 = !{!"omnipotent char", !5, i64 0}
!5 = !{!"Simple C/C++ TBAA"}
!6 = !{!7, !3, i64 0}
!7 = !{!"big", !3, i64 0, !3, i64 2, !3, i64 4, !3, i64 6, !3, i64 8}
!8 = !{!7, !3, i64 2}
!9 = !{!7, !3, i64 4}
!10 = !{!7, !3, i64 6}
!11 = !{!7, !3, i64 8}
