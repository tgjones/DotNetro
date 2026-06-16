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
	.file	"crc8.c"
	.section	.text.crc8,"ax",@progbits
	.globl	crc8                            ; -- Begin function crc8
	.type	crc8,@function
crc8:                                   ; @crc8
; %bb.0:
	sta	__rc4
	lda	#255
	stx	__rc5
	ldx	__rc5
	bne	.LBB0_2
; %bb.1:
	ldx	__rc4
	beq	.LBB0_11
.LBB0_2:
	ldx	#0
	ldy	__rc2
	sty	__rc8
	ldy	__rc3
	sty	__rc9
	stx	__rc2
	stx	__rc3
.LBB0_3:                                ; =>This Loop Header: Depth=1
                                        ;     Child Loop BB0_4 Depth 2
	clc
	tay
	lda	__rc8
	adc	__rc2
	sta	__rc6
	sty	__rc10
	lda	__rc9
	adc	__rc3
	sta	__rc7
	ldy	#0
	lda	__rc10
	eor	(__rc6),y
	ldx	#8
.LBB0_4:                                ;   Parent Loop BB0_3 Depth=1
                                        ; =>  This Inner Loop Header: Depth=2
	sta	__rc6
	asl
	ldy	__rc6
	bpl	.LBB0_6
; %bb.5:                                ;   in Loop: Header=BB0_4 Depth=2
	eor	#49
.LBB0_6:                                ;   in Loop: Header=BB0_4 Depth=2
	dex
	bne	.LBB0_4
; %bb.7:                                ;   in Loop: Header=BB0_3 Depth=1
	ldy	__rc3
	ldx	__rc2
	inx
	bne	.LBB0_9
; %bb.8:                                ;   in Loop: Header=BB0_3 Depth=1
	iny
.LBB0_9:                                ;   in Loop: Header=BB0_3 Depth=1
	stx	__rc2
	sty	__rc3
	cpy	__rc5
	bne	.LBB0_3
; %bb.10:                               ;   in Loop: Header=BB0_3 Depth=1
	stx	__rc2
	sty	__rc3
	cpx	__rc4
	bne	.LBB0_3
.LBB0_11:
	rts
.Lfunc_end0:
	.size	crc8, .Lfunc_end0-crc8
                                        ; -- End function
	.ident	"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"
	.section	".note.GNU-stack","",@progbits
	.addrsig
	;Declaring this symbol tells the CRT that the stack pointer needs to be initialized.
	.globl	__do_init_stack
