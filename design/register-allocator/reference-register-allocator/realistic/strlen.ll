; ModuleID = './realistic/strlen.c'
source_filename = "./realistic/strlen.c"
target datalayout = "e-m:e-p:16:8-p1:8:8-i16:8-i32:8-i64:8-f32:8-f64:8-a:8-Fi8-n8"
target triple = "mos-unknown-unknown"

; Function Attrs: mustprogress nofree norecurse nounwind willreturn memory(argmem: read)
define dso_local i16 @strlen_impl(ptr noundef readonly captures(none) %0) local_unnamed_addr #0 {
  %2 = tail call i16 @strlen(ptr noundef nonnull dereferenceable(1) %0)
  ret i16 %2
}

; Function Attrs: nocallback nofree nounwind willreturn memory(argmem: read)
declare i16 @strlen(ptr captures(none)) local_unnamed_addr #1

attributes #0 = { mustprogress nofree norecurse nounwind willreturn memory(argmem: read) "frame-pointer"="all" "no-trapping-math"="true" "stack-protector-buffer-size"="8" }
attributes #1 = { nocallback nofree nounwind willreturn memory(argmem: read) }

!llvm.module.flags = !{!0}
!llvm.ident = !{!1}
!llvm.errno.tbaa = !{!2}

!0 = !{i32 7, !"frame-pointer", i32 2}
!1 = !{!"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"}
!2 = !{!3, !3, i64 0}
!3 = !{!"int", !4, i64 0}
!4 = !{!"omnipotent char", !5, i64 0}
!5 = !{!"Simple C/C++ TBAA"}
