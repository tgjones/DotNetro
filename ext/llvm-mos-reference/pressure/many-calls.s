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
	.file	"many-calls.c"
	.section	.text.many_calls,"ax",@progbits
	.globl	many_calls                      ; -- Begin function many_calls
	.type	many_calls,@function
many_calls:                             ; @many_calls
; %bb.0:
	sta	__rc16
	clc
	lda	__rc0
	adc	#246
	sta	__rc0
	lda	__rc1
	adc	#255
	sta	__rc1
	lda	__rc20
	pha
	lda	__rc21
	pha
	lda	__rc22
	pha
	lda	__rc23
	pha
	lda	__rc16
	pha
	lda	__rc24
	ldy	#7
	sta	(__rc0),y                       ; 1-byte Folded Spill
	lda	__rc25
	dey
	sta	(__rc0),y                       ; 1-byte Folded Spill
	lda	__rc26
	dey
	sta	(__rc0),y                       ; 1-byte Folded Spill
	lda	__rc27
	dey
	sta	(__rc0),y                       ; 1-byte Folded Spill
	lda	__rc28
	dey
	sta	(__rc0),y                       ; 1-byte Folded Spill
	lda	__rc29
	dey
	sta	(__rc0),y                       ; 1-byte Folded Spill
	lda	__rc30
	dey
	sta	(__rc0),y                       ; 1-byte Folded Spill
	lda	__rc31
	dey
	sta	(__rc0),y                       ; 1-byte Folded Spill
	pla
	sta	__rc24
	stx	__rc25
	ldy	__rc2
	sty	__rc27
	ldy	__rc3
	sty	__rc20
	ldy	__rc4
	sty	__rc26
	ldy	__rc5
	sty	__rc21
	ldy	__rc6
	sty	__rc22
	ldy	__rc7
	sty	__rc23
	jsr	g
	ldy	#8
	sta	(__rc0),y                       ; 1-byte Folded Spill
	txa
	iny
	sta	(__rc0),y                       ; 1-byte Folded Spill
	ldx	__rc20
	lda	__rc27
	jsr	g
	sta	__rc28
	stx	__rc29
	ldx	__rc21
	lda	__rc26
	jsr	g
	sta	__rc30
	stx	__rc31
	ldx	__rc23
	lda	__rc22
	jsr	g
	sta	__rc2
	stx	__rc3
	lda	__rc27
	clc
	adc	__rc24
	tax
	lda	__rc20
	adc	__rc25
	sta	__rc4
	clc
	txa
	adc	__rc26
	sta	__rc5
	lda	__rc4
	adc	__rc21
	sta	__rc4
	clc
	lda	__rc5
	adc	__rc22
	sta	__rc8
	lda	__rc4
	adc	__rc23
	sta	__rc4
	ldy	#8
	lda	(__rc0),y                       ; 1-byte Folded Reload
	sta	__rc5
	clc
	lda	__rc8
	adc	__rc5
	sta	__rc5
	lda	__rc4
	pha
	iny
	lda	(__rc0),y                       ; 1-byte Folded Reload
	sta	__rc4
	pla
	adc	__rc4
	sta	__rc4
	clc
	lda	__rc5
	adc	__rc28
	sta	__rc5
	lda	__rc4
	adc	__rc29
	sta	__rc4
	clc
	lda	__rc5
	adc	__rc30
	tay
	lda	__rc4
	adc	__rc31
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
	ldy	#0
	lda	(__rc0),y                       ; 1-byte Folded Reload
	sta	__rc31
	iny
	lda	(__rc0),y                       ; 1-byte Folded Reload
	sta	__rc30
	iny
	lda	(__rc0),y                       ; 1-byte Folded Reload
	sta	__rc29
	iny
	lda	(__rc0),y                       ; 1-byte Folded Reload
	sta	__rc28
	iny
	lda	(__rc0),y                       ; 1-byte Folded Reload
	sta	__rc27
	iny
	lda	(__rc0),y                       ; 1-byte Folded Reload
	sta	__rc26
	iny
	lda	(__rc0),y                       ; 1-byte Folded Reload
	sta	__rc25
	iny
	lda	(__rc0),y                       ; 1-byte Folded Reload
	sta	__rc24
	pla
	sta	__rc23
	pla
	sta	__rc22
	pla
	sta	__rc21
	pla
	sta	__rc20
	clc
	lda	__rc0
	adc	#10
	sta	__rc0
	lda	__rc1
	adc	#0
	sta	__rc1
	lda	__rc16
	rts
.Lfunc_end0:
	.size	many_calls, .Lfunc_end0-many_calls
                                        ; -- End function
	.ident	"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"
	.section	".note.GNU-stack","",@progbits
	.addrsig
	;Declaring this symbol tells the CRT that the stack pointer needs to be initialized.
	.globl	__do_init_stack
