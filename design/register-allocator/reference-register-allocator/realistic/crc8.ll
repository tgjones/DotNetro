; ModuleID = './realistic/crc8.c'
source_filename = "./realistic/crc8.c"
target datalayout = "e-m:e-p:16:8-p1:8:8-i16:8-i32:8-i64:8-f32:8-f64:8-a:8-Fi8-n8"
target triple = "mos-unknown-unknown"

; Function Attrs: nofree norecurse nosync nounwind memory(argmem: read)
define dso_local zeroext i8 @crc8(ptr noundef readonly captures(none) %0, i16 noundef %1) local_unnamed_addr #0 {
  %3 = icmp eq i16 %1, 0
  br i1 %3, label %4, label %6

4:                                                ; preds = %58, %2
  %5 = phi i8 [ -1, %2 ], [ %59, %58 ]
  ret i8 %5

6:                                                ; preds = %2, %58
  %7 = phi i16 [ %60, %58 ], [ 0, %2 ]
  %8 = phi i8 [ %59, %58 ], [ -1, %2 ]
  %9 = getelementptr inbounds nuw i8, ptr %0, i16 %7
  %10 = load i8, ptr %9, align 1, !tbaa !6
  %11 = xor i8 %10, %8
  %12 = icmp sgt i8 %11, -1
  %13 = shl i8 %11, 1
  br i1 %12, label %16, label %14

14:                                               ; preds = %6
  %15 = xor i8 %13, 49
  br label %16

16:                                               ; preds = %6, %14
  %17 = phi i8 [ %15, %14 ], [ %13, %6 ]
  %18 = icmp sgt i8 %17, -1
  %19 = shl i8 %17, 1
  br i1 %18, label %22, label %20

20:                                               ; preds = %16
  %21 = xor i8 %19, 49
  br label %22

22:                                               ; preds = %16, %20
  %23 = phi i8 [ %21, %20 ], [ %19, %16 ]
  %24 = icmp sgt i8 %23, -1
  %25 = shl i8 %23, 1
  br i1 %24, label %28, label %26

26:                                               ; preds = %22
  %27 = xor i8 %25, 49
  br label %28

28:                                               ; preds = %22, %26
  %29 = phi i8 [ %27, %26 ], [ %25, %22 ]
  %30 = icmp sgt i8 %29, -1
  %31 = shl i8 %29, 1
  br i1 %30, label %34, label %32

32:                                               ; preds = %28
  %33 = xor i8 %31, 49
  br label %34

34:                                               ; preds = %28, %32
  %35 = phi i8 [ %33, %32 ], [ %31, %28 ]
  %36 = icmp sgt i8 %35, -1
  %37 = shl i8 %35, 1
  br i1 %36, label %40, label %38

38:                                               ; preds = %34
  %39 = xor i8 %37, 49
  br label %40

40:                                               ; preds = %34, %38
  %41 = phi i8 [ %39, %38 ], [ %37, %34 ]
  %42 = icmp sgt i8 %41, -1
  %43 = shl i8 %41, 1
  br i1 %42, label %46, label %44

44:                                               ; preds = %40
  %45 = xor i8 %43, 49
  br label %46

46:                                               ; preds = %40, %44
  %47 = phi i8 [ %45, %44 ], [ %43, %40 ]
  %48 = icmp sgt i8 %47, -1
  %49 = shl i8 %47, 1
  br i1 %48, label %52, label %50

50:                                               ; preds = %46
  %51 = xor i8 %49, 49
  br label %52

52:                                               ; preds = %46, %50
  %53 = phi i8 [ %51, %50 ], [ %49, %46 ]
  %54 = icmp sgt i8 %53, -1
  %55 = shl i8 %53, 1
  br i1 %54, label %58, label %56

56:                                               ; preds = %52
  %57 = xor i8 %55, 49
  br label %58

58:                                               ; preds = %52, %56
  %59 = phi i8 [ %57, %56 ], [ %55, %52 ]
  %60 = add nuw i16 %7, 1
  %61 = icmp eq i16 %60, %1
  br i1 %61, label %4, label %6, !llvm.loop !7
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
!6 = !{!4, !4, i64 0}
!7 = distinct !{!7, !8}
!8 = !{!"llvm.loop.mustprogress"}
