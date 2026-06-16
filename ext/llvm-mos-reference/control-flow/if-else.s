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
	.file	"if-else.c"
	.section	.text.if_else,"ax",@progbits
	.globl	if_else                         ; -- Begin function if_else
	.type	if_else,@function
if_else:                                ; @if_else
; %bb.0:
	sta	__rc4
	pha
	txa
	tay
	pla
	ldx	__rc2
	sta	__rc5
	cpx	__rc4
	lda	__rc3
	pha
	tya
	tax
	pla
	sty	__rc4
	sbc	__rc4
	bvc	.LBB0_2
; %bb.1:
	eor	#128
.LBB0_2:
	tay
	bpl	.LBB0_4
; %bb.3:
	sec
	lda	__rc5
	sbc	__rc2
	tay
	txa
	sbc	__rc3
	jmp	.LBB0_5
.LBB0_4:
	sec
	lda	__rc2
	sbc	__rc5
	tay
	lda	__rc3
	stx	__rc2
	sbc	__rc2
.LBB0_5:
	tax
	tya
	rts
.Lfunc_end0:
	.size	if_else, .Lfunc_end0-if_else
                                        ; -- End function
	.ident	"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"
	.section	".note.GNU-stack","",@progbits
	.addrsig
	;Declaring this symbol tells the CRT that the stack pointer needs to be initialized.
	.globl	__do_init_stack
