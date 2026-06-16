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
	.file	"factorial-recursive.c"
	.section	.text.factorial,"ax",@progbits
	.globl	factorial                       ; -- Begin function factorial
	.type	factorial,@function
factorial:                              ; @factorial
; %bb.0:
	tay
	cmp	#2
	txa
	sbc	#0
	bvc	.LBB0_2
; %bb.1:
	eor	#128
.LBB0_2:
	cmp	#0
	bpl	.LBB0_4
; %bb.3:
	ldx	#0
	lda	#1
	rts
.LBB0_4:
	lda	__rc20
	pha
	lda	__rc21
	pha
	lda	__rc22
	pha
	lda	#1
	stx	__rc22
	ldx	#0
.LBB0_5:                                ; =>This Inner Loop Header: Depth=1
	sty	__rc21
	pha
	lda	__rc22
	sta	__rc20
	pla
	sty	__rc5
	ldy	#255
	dec	__rc21
	cpy	__rc21
	bne	.LBB0_7
; %bb.6:                                ;   in Loop: Header=BB0_5 Depth=1
	dec	__rc20
.LBB0_7:                                ;   in Loop: Header=BB0_5 Depth=1
	sta	__rc2
	stx	__rc3
	ldx	__rc22
	ldy	__rc5
	sty	.Lfactorial_sstk                ; 1-byte Folded Spill
	lda	__rc5
	jsr	__mulhi3
	ldy	__rc22
	bne	.LBB0_9
; %bb.8:                                ;   in Loop: Header=BB0_5 Depth=1
	ldy	.Lfactorial_sstk                ; 1-byte Folded Reload
	cpy	#3
	jmp	.LBB0_10
.LBB0_9:                                ;   in Loop: Header=BB0_5 Depth=1
	ldy	__rc22
	cpy	#0
.LBB0_10:                               ;   in Loop: Header=BB0_5 Depth=1
	ldy	__rc21
	pha
	lda	__rc20
	sta	__rc22
	pla
	bcs	.LBB0_5
; %bb.11:
	sta	__rc16
	pla
	sta	__rc22
	pla
	sta	__rc21
	pla
	sta	__rc20
	lda	__rc16
	rts
.Lfunc_end0:
	.size	factorial, .Lfunc_end0-factorial
                                        ; -- End function
	.type	.Lstatic_stack,@object          ; @static_stack
	.section	.noinit..Lstatic_stack,"aw",@nobits
.Lstatic_stack:
	.zero	1
	.size	.Lstatic_stack, 1

.Lfactorial_sstk = .Lstatic_stack
	.size	.Lfactorial_sstk, 1
	.ident	"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"
	.section	".note.GNU-stack","",@progbits
	.addrsig
	.addrsig_sym .Lstatic_stack
	;Declaring this symbol tells the CRT that the stack pointer needs to be initialized.
	.globl	__do_init_stack
