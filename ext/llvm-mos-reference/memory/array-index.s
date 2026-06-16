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
	.file	"array-index.c"
	.section	.text.array_index,"ax",@progbits
	.globl	array_index                     ; -- Begin function array_index
	.type	array_index,@function
array_index:                            ; @array_index
; %bb.0:
	stx	__rc4
	asl
	rol	__rc4
	clc
	adc	__rc2
	tay
	lda	__rc3
	adc	__rc4
	tax
	sty	__rc2
	sty	__rc4
	sta	__rc3
	ldy	#0
	lda	(__rc2),y
	sta	__rc6
	clc
	lda	__rc4
	adc	#2
	sta	__rc4
	txa
	adc	#0
	sta	__rc5
	iny
	lda	(__rc2),y
	tax
	sty	__rc7
	iny
	clc
	lda	__rc6
	adc	(__rc2),y
	sta	__rc2
	txa
	ldy	__rc7
	adc	(__rc4),y
	tax
	lda	__rc2
	rts
.Lfunc_end0:
	.size	array_index, .Lfunc_end0-array_index
                                        ; -- End function
	.ident	"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"
	.section	".note.GNU-stack","",@progbits
	.addrsig
	;Declaring this symbol tells the CRT that the stack pointer needs to be initialized.
	.globl	__do_init_stack
