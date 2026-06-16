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
	.file	"bitops.c"
	.section	.text.bitops,"ax",@progbits
	.globl	bitops                          ; -- Begin function bitops
	.type	bitops,@function
bitops:                                 ; @bitops
; %bb.0:
	sta	__rc6
	stx	__rc7
	ldx	__rc4
	ldy	__rc5
	lda	__rc4
	eor	__rc2
	sta	__rc4
	lda	__rc5
	eor	__rc3
	sta	__rc5
	txa
	ora	__rc2
	tax
	tya
	ora	__rc3
	sta	__rc2
	txa
	and	__rc6
	tay
	lda	__rc2
	and	__rc7
	tax
	tya
	ora	__rc4
	tay
	txa
	ora	__rc5
	tax
	tya
	rts
.Lfunc_end0:
	.size	bitops, .Lfunc_end0-bitops
                                        ; -- End function
	.ident	"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"
	.section	".note.GNU-stack","",@progbits
	.addrsig
	;Declaring this symbol tells the CRT that the stack pointer needs to be initialized.
	.globl	__do_init_stack
