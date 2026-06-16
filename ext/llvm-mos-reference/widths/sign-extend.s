	.zeropage	__rc0
	.zeropage	__rc1
	.zeropage	__rc2
	.zeropage	__rc3
	.zeropage	__rc4
	.zeropage	__rc5
	.zeropage	__rc6
	.zeropage	__rc7
	.zeropage	__rc8
	.zeropage	__rc9
	.zeropage	__rc10
	.zeropage	__rc11
	.zeropage	__rc12
	.zeropage	__rc13
	.zeropage	__rc14
	.zeropage	__rc15
	.zeropage	__rc16
	.zeropage	__rc17
	.zeropage	__rc18
	.zeropage	__rc19
	.zeropage	__rc20
	.zeropage	__rc21
	.zeropage	__rc22
	.zeropage	__rc23
	.zeropage	__rc24
	.zeropage	__rc25
	.zeropage	__rc26
	.zeropage	__rc27
	.zeropage	__rc28
	.zeropage	__rc29
	.zeropage	__rc30
	.zeropage	__rc31
	.file	"sign-extend.c"
	.section	.text.sign_extend,"ax",@progbits
	.globl	sign_extend                     ; -- Begin function sign_extend
	.type	sign_extend,@function
sign_extend:                            ; @sign_extend
; %bb.0:
	tax
	bpl	.LBB0_2
; %bb.1:
	ldx	#255
	jmp	.LBB0_3
.LBB0_2:
	ldx	#0
.LBB0_3:
	clc
	adc	#255
	cmp	#255
	bne	.LBB0_5
; %bb.4:
	dex
.LBB0_5:
	rts
.Lfunc_end0:
	.size	sign_extend, .Lfunc_end0-sign_extend
                                        ; -- End function
	.ident	"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"
	.section	".note.GNU-stack","",@progbits
	.addrsig
	;Declaring this symbol tells the CRT that the stack pointer needs to be initialized.
	.globl	__do_init_stack
