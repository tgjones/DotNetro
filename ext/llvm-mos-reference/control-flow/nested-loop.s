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
	.file	"nested-loop.c"
	.section	.text.nested_loop,"ax",@progbits
	.globl	nested_loop                     ; -- Begin function nested_loop
	.type	nested_loop,@function
nested_loop:                            ; @nested_loop
; %bb.0:
	sta	__rc16
	lda	__rc20
	pha
	lda	__rc22
	pha
	lda	__rc16
	tay
	sty	__rc17
	ldy	__rc2
	sty	__rc20
	ldy	__rc17
	cmp	#1
	txa
	sbc	#0
	bvc	.LBB0_2
; %bb.1:
	eor	#128
.LBB0_2:
	cmp	#0
	bpl	.LBB0_3
	jmp	.LBB0_19
.LBB0_3:
	lda	__rc2
	sta	__rc9
	lda	__rc3
	sta	__rc8
	sty	.Lnested_loop_sstk+2            ; 1-byte Folded Spill
	ldy	#255
	dec	__rc9
	cpy	__rc9
	bne	.LBB0_5
; %bb.4:
	dec	__rc8
.LBB0_5:
	lda	__rc2
	clc
	adc	#254
	sta	__rc4
	lda	__rc3
	adc	#255
	ldy	#0
	sty	__rc2
	sty	__rc5
	ldy	__rc3
	sty	.Lnested_loop_sstk              ; 1-byte Folded Spill
	ldy	__rc5
	sty	__rc3
	sta	__rc5
	sty	__rc6
	sty	__rc7
	sty	__rc22
	stx	.Lnested_loop_sstk+1            ; 1-byte Folded Spill
	ldx	__rc8
	lda	__rc9
	jsr	__mulsi3
	ldy	.Lnested_loop_sstk              ; 1-byte Folded Reload
	sty	__rc3
	sta	__rc4
	stx	__rc5
	ldx	.Lnested_loop_sstk+1            ; 1-byte Folded Reload
	stx	__rc6
	lda	__rc2
	and	#1
	lsr
	ror	__rc5
	ror	__rc4
	lda	__rc20
	ldx	#0
	stx	__rc9
	clc
	adc	__rc4
	sta	__rc7
	tya
	adc	__rc5
	sta	__rc8
	ldy	.Lnested_loop_sstk+2            ; 1-byte Folded Reload
	ldx	__rc22
	stx	__rc2
	ldx	__rc22
	stx	__rc5
	ldx	__rc22
	stx	__rc4
	ldx	#0
	stx	__rc10
	jmp	.LBB0_13
.LBB0_6:                                ;   in Loop: Header=BB0_13 Depth=1
	ldx	__rc9
	cpx	#1
.LBB0_7:                                ;   in Loop: Header=BB0_13 Depth=1
	lda	__rc7
	ldx	#0
	stx	__rc9
	adc	__rc2
	sta	__rc2
	lda	__rc8
	adc	__rc4
	ldx	#255
	dec	__rc2
	cpx	__rc2
	bne	.LBB0_9
; %bb.8:                                ;   in Loop: Header=BB0_13 Depth=1
	clc
	adc	#255
.LBB0_9:                                ;   in Loop: Header=BB0_13 Depth=1
	dey
	cpy	#255
	bne	.LBB0_11
; %bb.10:                               ;   in Loop: Header=BB0_13 Depth=1
	dec	__rc6
.LBB0_11:                               ;   in Loop: Header=BB0_13 Depth=1
	sta	__rc4
	lda	__rc6
	bne	.LBB0_13
; %bb.12:                               ;   in Loop: Header=BB0_13 Depth=1
	tya
	beq	.LBB0_20
.LBB0_13:                               ; =>This Inner Loop Header: Depth=1
	ldx	__rc20
	cpx	#1
	lda	__rc3
	sbc	#0
	bvc	.LBB0_15
; %bb.14:                               ;   in Loop: Header=BB0_13 Depth=1
	eor	#128
.LBB0_15:                               ;   in Loop: Header=BB0_13 Depth=1
	tax
	bmi	.LBB0_6
; %bb.16:                               ;   in Loop: Header=BB0_13 Depth=1
	lda	__rc5
	clc
	ldx	#1
	bcs	.LBB0_18
; %bb.17:                               ;   in Loop: Header=BB0_13 Depth=1
	ldx	#0
.LBB0_18:                               ;   in Loop: Header=BB0_13 Depth=1
	stx	__rc9
	adc	__rc2
	sta	__rc5
	lda	__rc10
	adc	__rc4
	ldx	__rc9
	cpx	#1
	sta	__rc10
	jmp	.LBB0_7
.LBB0_19:
	lda	#0
	sta	__rc5
	tax
	stx	__rc10
.LBB0_20:
	ldx	__rc10
	lda	__rc5
	sta	__rc16
	pla
	sta	__rc22
	pla
	sta	__rc20
	lda	__rc16
	rts
.Lfunc_end0:
	.size	nested_loop, .Lfunc_end0-nested_loop
                                        ; -- End function
	.type	.Lstatic_stack,@object          ; @static_stack
	.section	.noinit..Lstatic_stack,"aw",@nobits
.Lstatic_stack:
	.zero	3
	.size	.Lstatic_stack, 3

.Lnested_loop_sstk = .Lstatic_stack
	.size	.Lnested_loop_sstk, 3
	.ident	"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"
	.section	".note.GNU-stack","",@progbits
	.addrsig
	.addrsig_sym .Lstatic_stack
	;Declaring this symbol tells the CRT that the stack pointer needs to be initialized.
	.globl	__do_init_stack
