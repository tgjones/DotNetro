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
	.file	"ptr-deref.c"
	.section	.text.ptr_deref,"ax",@progbits
	.globl	ptr_deref                       ; -- Begin function ptr_deref
	.type	ptr_deref,@function
ptr_deref:                              ; @ptr_deref
; %bb.0:
	ldy	#0
	lda	(__rc2),y
	ldx	#0
	stx	__rc5
	sta	__rc4
	iny
	lda	(__rc2),y
	inx
	stx	__rc6
	tax
	lda	__rc4
	stx	__rc7
	clc
	adc	#1
	bne	.LBB0_2
; %bb.1:
	inc	__rc7
.LBB0_2:
	ldy	__rc5
	sta	(__rc2),y
	ldy	__rc6
	lda	__rc7
	sta	(__rc2),y
	lda	__rc4
	rts
.Lfunc_end0:
	.size	ptr_deref, .Lfunc_end0-ptr_deref
                                        ; -- End function
	.ident	"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"
	.section	".note.GNU-stack","",@progbits
	.addrsig
	;Declaring this symbol tells the CRT that the stack pointer needs to be initialized.
	.globl	__do_init_stack
