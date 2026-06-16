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
	.file	"pressure-high.c"
	.section	.text.pressure_high,"ax",@progbits
	.globl	pressure_high                   ; -- Begin function pressure_high
	.type	pressure_high,@function
pressure_high:                          ; @pressure_high
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
	ldy	__rc24
	sty	.Lpressure_high_sstk+8          ; 1-byte Folded Spill
	ldy	__rc25
	sty	.Lpressure_high_sstk+9          ; 1-byte Folded Spill
	ldy	__rc26
	sty	.Lpressure_high_sstk+10         ; 1-byte Folded Spill
	ldy	__rc27
	sty	.Lpressure_high_sstk+11         ; 1-byte Folded Spill
	ldy	__rc28
	sty	.Lpressure_high_sstk+12         ; 1-byte Folded Spill
	ldy	__rc29
	sty	.Lpressure_high_sstk+13         ; 1-byte Folded Spill
	ldy	__rc30
	sty	.Lpressure_high_sstk+14         ; 1-byte Folded Spill
	ldy	__rc31
	sty	.Lpressure_high_sstk+15         ; 1-byte Folded Spill
	sta	__rc24
	stx	__rc25
	ldx	__rc2
	stx	__rc26
	ldx	__rc3
	stx	__rc27
	ldx	__rc4
	stx	.Lpressure_high_sstk            ; 1-byte Folded Spill
	ldx	__rc5
	stx	.Lpressure_high_sstk+1          ; 1-byte Folded Spill
	ldx	__rc6
	stx	.Lpressure_high_sstk+2          ; 1-byte Folded Spill
	ldx	__rc7
	stx	.Lpressure_high_sstk+3          ; 1-byte Folded Spill
	ldx	__rc8
	stx	__rc21
	ldx	__rc9
	stx	__rc30
	ldx	__rc10
	stx	__rc20
	ldx	__rc11
	stx	__rc31
	ldx	__rc12
	stx	__rc22
	ldx	__rc13
	stx	__rc23
	ldx	__rc14
	stx	__rc28
	ldx	__rc15
	stx	__rc29
	ldx	__rc14
	stx	__rc2
	ldx	__rc15
	stx	__rc3
	ldx	__rc8
	stx	__rc4
	ldx	__rc9
	stx	__rc5
	ldx	__rc10
	stx	__rc6
	ldx	__rc11
	stx	__rc7
	ldx	__rc13
	lda	__rc12
	jsr	__mulsi3
	sta	.Lpressure_high_sstk+7          ; 1-byte Folded Spill
	stx	.Lpressure_high_sstk+6          ; 1-byte Folded Spill
	ldx	__rc2
	stx	.Lpressure_high_sstk+5          ; 1-byte Folded Spill
	ldx	__rc3
	stx	.Lpressure_high_sstk+4          ; 1-byte Folded Spill
	ldx	__rc28
	stx	__rc2
	ldx	__rc29
	stx	__rc3
	ldx	__rc24
	stx	__rc4
	ldx	__rc25
	stx	__rc5
	ldx	__rc26
	stx	__rc6
	ldx	__rc27
	stx	__rc7
	ldx	__rc23
	lda	__rc22
	jsr	__mulsi3
	sta	__rc29
	stx	__rc28
	ldx	__rc2
	stx	__rc23
	ldx	__rc3
	stx	__rc22
	clc
	lda	__rc21
	adc	__rc24
	tay
	lda	__rc30
	adc	__rc25
	sta	__rc8
	lda	__rc20
	adc	__rc26
	sta	__rc2
	lda	__rc31
	adc	__rc27
	sta	__rc3
	ldx	.Lpressure_high_sstk            ; 1-byte Folded Reload
	stx	__rc4
	ldx	.Lpressure_high_sstk+1          ; 1-byte Folded Reload
	stx	__rc5
	ldx	.Lpressure_high_sstk+2          ; 1-byte Folded Reload
	stx	__rc6
	ldx	.Lpressure_high_sstk+3          ; 1-byte Folded Reload
	stx	__rc7
	ldx	__rc8
	tya
	jsr	__mulsi3
	sta	__rc24
	stx	__rc27
	ldx	__rc2
	stx	__rc25
	ldx	__rc3
	stx	__rc26
	ldx	.Lpressure_high_sstk+7          ; 1-byte Folded Reload
	stx	__rc31
	txa
	clc
	adc	__rc29
	tay
	ldx	.Lpressure_high_sstk+6          ; 1-byte Folded Reload
	stx	__rc30
	txa
	adc	__rc28
	sta	__rc8
	ldx	.Lpressure_high_sstk+5          ; 1-byte Folded Reload
	stx	__rc21
	txa
	adc	__rc23
	sta	__rc2
	ldx	.Lpressure_high_sstk+4          ; 1-byte Folded Reload
	stx	__rc20
	txa
	adc	__rc22
	sta	__rc3
	ldx	__rc24
	stx	__rc4
	ldx	__rc27
	stx	__rc5
	ldx	__rc25
	stx	__rc6
	ldx	__rc26
	stx	__rc7
	ldx	__rc8
	tya
	jsr	__mulsi3
	sta	__rc4
	stx	__rc5
	clc
	lda	__rc24
	adc	__rc31
	tax
	lda	__rc27
	adc	__rc30
	sta	__rc8
	lda	__rc25
	adc	__rc21
	sta	__rc6
	lda	__rc26
	adc	__rc20
	sta	__rc7
	clc
	txa
	adc	__rc29
	tay
	lda	__rc8
	adc	__rc28
	sta	__rc8
	lda	__rc6
	adc	__rc23
	sta	__rc6
	lda	__rc7
	adc	__rc22
	tax
	clc
	tya
	adc	__rc4
	tay
	lda	__rc8
	adc	__rc5
	sta	__rc4
	lda	__rc6
	adc	__rc2
	sta	__rc2
	txa
	adc	__rc3
	sta	__rc3
	ldx	__rc4
	tya
	sta	__rc16
	ldy	.Lpressure_high_sstk+15         ; 1-byte Folded Reload
	sty	__rc31
	ldy	.Lpressure_high_sstk+14         ; 1-byte Folded Reload
	sty	__rc30
	ldy	.Lpressure_high_sstk+13         ; 1-byte Folded Reload
	sty	__rc29
	ldy	.Lpressure_high_sstk+12         ; 1-byte Folded Reload
	sty	__rc28
	ldy	.Lpressure_high_sstk+11         ; 1-byte Folded Reload
	sty	__rc27
	ldy	.Lpressure_high_sstk+10         ; 1-byte Folded Reload
	sty	__rc26
	ldy	.Lpressure_high_sstk+9          ; 1-byte Folded Reload
	sty	__rc25
	ldy	.Lpressure_high_sstk+8          ; 1-byte Folded Reload
	sty	__rc24
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
	.size	pressure_high, .Lfunc_end0-pressure_high
                                        ; -- End function
	.type	.Lstatic_stack,@object          ; @static_stack
	.section	.noinit..Lstatic_stack,"aw",@nobits
.Lstatic_stack:
	.zero	16
	.size	.Lstatic_stack, 16

.Lpressure_high_sstk = .Lstatic_stack
	.size	.Lpressure_high_sstk, 16
	.ident	"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"
	.section	".note.GNU-stack","",@progbits
	.addrsig
	.addrsig_sym .Lstatic_stack
	;Declaring this symbol tells the CRT that the stack pointer needs to be initialized.
	.globl	__do_init_stack
