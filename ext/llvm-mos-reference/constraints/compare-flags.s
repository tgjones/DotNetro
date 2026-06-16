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
	.file	"compare-flags.c"
	.section	.text.compare_flags,"ax",@progbits
	.globl	compare_flags                   ; -- Begin function compare_flags
	.type	compare_flags,@function
compare_flags:                          ; @compare_flags
; %bb.0:
	ldy	__rc2
	sta	__rc7
	cmp	__rc2
	stx	__rc6
	txa
	sbc	__rc3
	bvc	.LBB0_2
; %bb.1:
	eor	#128
.LBB0_2:
	tax
	bpl	.LBB0_4
; %bb.3:
	ldx	#0
	lda	#1
	jmp	.LBB0_5
.LBB0_4:
	ldx	#0
	txa
.LBB0_5:
	sta	__rc2
	cpy	__rc4
	lda	__rc3
	sbc	__rc5
	bvc	.LBB0_7
; %bb.6:
	eor	#128
.LBB0_7:
	tax
	bpl	.LBB0_9
; %bb.8:
	bit	__set_v
	jmp	.LBB0_10
.LBB0_9:
	clv
.LBB0_10:
	ldy	#1
	tya
	ldx	__rc2
	bne	.LBB0_12
; %bb.11:
	lda	#0
.LBB0_12:
	sty	__rc2
	bvs	.LBB0_14
; %bb.13:
	ldx	#0
	stx	__rc2
.LBB0_14:
	and	__rc2
	sta	__rc2
	ldx	__rc6
	cpx	__rc5
	bne	.LBB0_17
; %bb.15:
	ldx	__rc7
	cpx	__rc4
	bne	.LBB0_17
; %bb.16:
	ldy	#0
.LBB0_17:
	tya
	and	__rc2
	rts
.Lfunc_end0:
	.size	compare_flags, .Lfunc_end0-compare_flags
                                        ; -- End function
	.ident	"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"
	.section	".note.GNU-stack","",@progbits
	.addrsig
	;Declaring this symbol tells the CRT that the stack pointer needs to be initialized.
	.globl	__do_init_stack
