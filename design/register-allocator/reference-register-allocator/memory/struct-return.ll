; ModuleID = './memory/struct-return.c'
source_filename = "./memory/struct-return.c"
target datalayout = "e-m:e-p:16:8-p1:8:8-i16:8-i32:8-i64:8-f32:8-f64:8-a:8-Fi8-n8"
target triple = "mos-unknown-unknown"

%struct.pair = type { i16, i16 }

; Function Attrs: mustprogress nofree norecurse nosync nounwind willreturn memory(none)
define dso_local %struct.pair @struct_return(i16 noundef %0) local_unnamed_addr #0 {
  %2 = add nsw i16 %0, 1
  %3 = add nsw i16 %0, -1
  %4 = insertvalue %struct.pair poison, i16 %2, 0
  %5 = insertvalue %struct.pair %4, i16 %3, 1
  ret %struct.pair %5
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
