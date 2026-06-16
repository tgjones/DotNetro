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
	.file	"live-across-call.c"
	.section	.text.live_across_call,"ax",@progbits
	.globl	live_across_call                ; -- Begin function live_across_call
	.type	live_across_call,@function
live_across_call:                       ; @live_across_call
; %bb.0:
	sta	__rc16
	lda	__rc20
	pha
	lda	__rc21
	pha
	lda	__rc16
	sta	__rc20
	stx	__rc21
	jsr	g
	clc
	adc	__rc20
	tay
	txa
	adc	__rc21
	tax
	tya
	sta	__rc16
	pla
	sta	__rc21
	pla
	sta	__rc20
	lda	__rc16
	rts
.Lfunc_end0:
	.size	live_across_call, .Lfunc_end0-live_across_call
                                        ; -- End function
	.ident	"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"
	.section	".note.GNU-stack","",@progbits
	.addrsig
	;Declaring this symbol tells the CRT that the stack pointer needs to be initialized.
	.globl	__do_init_stack
