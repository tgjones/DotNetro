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
	.file	"shift-const.c"
	.section	.text.shift_const,"ax",@progbits
	.globl	shift_const                     ; -- Begin function shift_const
	.type	shift_const,@function
shift_const:                            ; @shift_const
; %bb.0:
	tay
	txa
	sty	__rc2
	asl	__rc2
	stx	__rc3
	rol	__rc3
	asl	__rc2
	rol	__rc3
	asl	__rc2
	rol	__rc3
	cpx	#128
	ror
	sty	__rc4
	ror	__rc4
	cmp	#128
	ror
	tax
	lda	__rc4
	ror
	ora	__rc2
	tay
	txa
	ora	__rc3
	tax
	tya
	rts
.Lfunc_end0:
	.size	shift_const, .Lfunc_end0-shift_const
                                        ; -- End function
	.ident	"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"
	.section	".note.GNU-stack","",@progbits
	.addrsig
	;Declaring this symbol tells the CRT that the stack pointer needs to be initialized.
	.globl	__do_init_stack
