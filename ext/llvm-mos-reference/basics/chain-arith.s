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
	.file	"chain-arith.c"
	.section	.text.chain_arith,"ax",@progbits
	.globl	chain_arith                     ; -- Begin function chain_arith
	.type	chain_arith,@function
chain_arith:                            ; @chain_arith
; %bb.0:
	sta	__rc16
	lda	__rc20
	pha
	lda	__rc21
	pha
	lda	__rc22
	pha
	lda	__rc23
	pha
	lda	__rc16
	sta	__rc20
	stx	__rc21
	lda	__rc2
	ldx	__rc3
	ldy	__rc4
	sty	__rc22
	ldy	__rc5
	sty	__rc23
	ldy	__rc20
	sty	__rc2
	ldy	__rc21
	sty	__rc3
	jsr	__mulhi3
	sta	__rc2
	stx	__rc3
	sec
	lda	__rc22
	sbc	__rc20
	tay
	lda	__rc23
	sbc	__rc21
	tax
	clc
	tya
	adc	__rc2
	tay
	txa
	adc	__rc3
	tax
	tya
	sta	__rc16
	pla
	sta	__rc23
	pla
	sta	__rc22
	pla
	sta	__rc21
	pla
	sta	__rc20
	lda	__rc16
	rts
.Lfunc_end0:
	.size	chain_arith, .Lfunc_end0-chain_arith
                                        ; -- End function
	.ident	"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"
	.section	".note.GNU-stack","",@progbits
	.addrsig
	;Declaring this symbol tells the CRT that the stack pointer needs to be initialized.
	.globl	__do_init_stack
