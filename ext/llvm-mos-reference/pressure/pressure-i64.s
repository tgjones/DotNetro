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
	.file	"pressure-i64.c"
	.section	.text.pressure_i64,"ax",@progbits
	.globl	pressure_i64                    ; -- Begin function pressure_i64
	.type	pressure_i64,@function
pressure_i64:                           ; @pressure_i64
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
	sty	.Lpressure_i64_sstk+15          ; 1-byte Folded Spill
	ldy	__rc25
	sty	.Lpressure_i64_sstk+16          ; 1-byte Folded Spill
	ldy	__rc26
	sty	.Lpressure_i64_sstk+17          ; 1-byte Folded Spill
	ldy	__rc27
	sty	.Lpressure_i64_sstk+18          ; 1-byte Folded Spill
	ldy	__rc28
	sty	.Lpressure_i64_sstk+19          ; 1-byte Folded Spill
	ldy	__rc29
	sty	.Lpressure_i64_sstk+20          ; 1-byte Folded Spill
	ldy	__rc30
	sty	.Lpressure_i64_sstk+21          ; 1-byte Folded Spill
	ldy	__rc31
	sty	.Lpressure_i64_sstk+22          ; 1-byte Folded Spill
	sta	__rc27
	stx	__rc25
	ldx	__rc2
	stx	__rc26
	ldx	__rc3
	stx	.Lpressure_i64_sstk+13          ; 1-byte Folded Spill
	ldx	__rc4
	stx	.Lpressure_i64_sstk+12          ; 1-byte Folded Spill
	ldx	__rc5
	stx	.Lpressure_i64_sstk+11          ; 1-byte Folded Spill
	ldx	__rc6
	stx	.Lpressure_i64_sstk+1           ; 1-byte Folded Spill
	ldx	__rc7
	stx	.Lpressure_i64_sstk+10          ; 1-byte Folded Spill
	ldx	__rc8
	stx	__rc20
	ldx	__rc9
	stx	__rc21
	ldx	__rc10
	stx	__rc24
	ldx	__rc13
	stx	__rc10
	ldx	__rc14
	stx	__rc8
	ldy	__rc15
	sty	.Lpressure_i64_sstk+2           ; 1-byte Folded Spill
	ldy	#0
	clc
	ldx	__rc0
	stx	__rc2
	ldx	__rc1
	stx	__rc3
	lda	(__rc2),y
	sta	.Lpressure_i64_sstk             ; 1-byte Folded Spill
	lda	__rc0
	adc	#1
	sta	__rc2
	lda	__rc1
	adc	#0
	sta	__rc3
	lda	(__rc2),y
	sta	__rc15
	clc
	lda	__rc0
	adc	#2
	sta	__rc28
	lda	__rc1
	adc	#0
	sta	__rc29
	clc
	lda	__rc0
	adc	#3
	sta	__rc30
	lda	__rc1
	adc	#0
	sta	__rc31
	clc
	lda	__rc0
	adc	#4
	sta	__rc18
	lda	__rc1
	adc	#0
	sta	__rc19
	clc
	lda	__rc0
	adc	#5
	sta	__rc22
	lda	__rc1
	adc	#0
	sta	__rc23
	clc
	lda	__rc0
	adc	#6
	sta	__rc4
	lda	__rc1
	adc	#0
	sta	__rc5
	clc
	lda	__rc0
	adc	#7
	sta	__rc2
	lda	__rc1
	adc	#0
	sta	__rc3
	clc
	lda	__rc20
	adc	__rc27
	sta	.Lpressure_i64_sstk+3           ; 1-byte Folded Spill
	lda	__rc9
	adc	__rc25
	sta	.Lpressure_i64_sstk+5           ; 1-byte Folded Spill
	lda	__rc24
	adc	__rc26
	sta	.Lpressure_i64_sstk+4           ; 1-byte Folded Spill
	lda	__rc11
	ldx	__rc11
	stx	__rc14
	ldx	.Lpressure_i64_sstk+13          ; 1-byte Folded Reload
	stx	__rc7
	adc	__rc7
	sta	.Lpressure_i64_sstk+6           ; 1-byte Folded Spill
	lda	__rc12
	ldx	__rc12
	stx	__rc13
	ldx	.Lpressure_i64_sstk+12          ; 1-byte Folded Reload
	stx	__rc12
	adc	__rc12
	sta	.Lpressure_i64_sstk+7           ; 1-byte Folded Spill
	lda	__rc10
	ldx	.Lpressure_i64_sstk+11          ; 1-byte Folded Reload
	stx	__rc9
	adc	__rc9
	sta	.Lpressure_i64_sstk+8           ; 1-byte Folded Spill
	ldx	__rc8
	stx	__rc6
	lda	__rc8
	ldx	.Lpressure_i64_sstk+1           ; 1-byte Folded Reload
	stx	__rc8
	adc	__rc8
	ldx	#1
	bcs	.LBB0_2
; %bb.1:
	ldx	#0
.LBB0_2:
	stx	__rc11
	sta	.Lpressure_i64_sstk+9           ; 1-byte Folded Spill
	lda	(__rc28),y
	sta	__rc29
	lda	(__rc30),y
	sta	__rc28
	lda	(__rc18),y
	sta	__rc18
	lda	(__rc22),y
	tax
	lda	(__rc4),y
	sta	__rc4
	lda	(__rc2),y
	sta	__rc5
	lda	.Lpressure_i64_sstk             ; 1-byte Folded Reload
	clc
	adc	__rc20
	sta	__rc22
	lda	__rc15
	adc	__rc21
	sta	__rc20
	lda	__rc29
	adc	__rc24
	sta	__rc19
	lda	__rc28
	adc	__rc14
	sta	__rc24
	lda	__rc18
	adc	__rc13
	sta	__rc23
	txa
	adc	__rc10
	sta	__rc10
	lda	__rc4
	adc	__rc6
	sta	__rc21
	lda	__rc5
	ldy	.Lpressure_i64_sstk+2           ; 1-byte Folded Reload
	sty	__rc2
	adc	__rc2
	sta	__rc30
	tya
	ldy	.Lpressure_i64_sstk+10          ; 1-byte Folded Reload
	sty	__rc2
	ldy	__rc11
	cpy	#1
	adc	__rc2
	sta	.Lpressure_i64_sstk+14          ; 1-byte Folded Spill
	lda	.Lpressure_i64_sstk             ; 1-byte Folded Reload
	clc
	adc	__rc27
	sta	.Lpressure_i64_sstk             ; 1-byte Folded Spill
	sta	__rc6
	lda	__rc15
	adc	__rc25
	sta	__rc3
	lda	__rc29
	adc	__rc26
	sta	__rc26
	lda	__rc28
	adc	__rc7
	tay
	lda	__rc18
	adc	__rc12
	sta	__rc31
	txa
	adc	__rc9
	sta	.Lpressure_i64_sstk+2           ; 1-byte Folded Spill
	lda	__rc4
	adc	__rc8
	sta	.Lpressure_i64_sstk+1           ; 1-byte Folded Spill
	lda	__rc5
	adc	__rc2
	sta	__rc25
	lda	__rc22
	sta	__rc12
	eor	__rc6
	sta	__rc2
	ldx	__rc3
	stx	__rc22
	lda	__rc20
	sta	__rc13
	eor	__rc3
	sta	__rc3
	lda	__rc19
	sta	__rc14
	eor	__rc26
	sta	__rc4
	sty	__rc5
	lda	__rc24
	sta	__rc8
	eor	__rc5
	sta	__rc5
	lda	__rc23
	sta	__rc18
	eor	__rc31
	sta	__rc6
	ldx	.Lpressure_i64_sstk+2           ; 1-byte Folded Reload
	stx	__rc7
	lda	__rc10
	sta	__rc19
	eor	__rc7
	sta	__rc7
	ldx	.Lpressure_i64_sstk+1           ; 1-byte Folded Reload
	stx	__rc9
	lda	__rc21
	sta	__rc27
	eor	__rc9
	sta	__rc9
	lda	__rc30
	sta	__rc28
	eor	__rc25
	sta	__rc10
	ldx	.Lpressure_i64_sstk+3           ; 1-byte Folded Reload
	stx	__rc11
	lda	__rc12
	eor	__rc11
	sta	__rc15
	ldx	.Lpressure_i64_sstk+5           ; 1-byte Folded Reload
	stx	__rc12
	lda	__rc13
	eor	__rc12
	sta	__rc13
	clc
	lda	__rc15
	adc	__rc2
	sta	__rc29
	lda	__rc13
	adc	__rc3
	sta	__rc24
	ldx	.Lpressure_i64_sstk+4           ; 1-byte Folded Reload
	stx	__rc13
	lda	__rc14
	eor	__rc13
	adc	__rc4
	sta	__rc30
	ldx	.Lpressure_i64_sstk+6           ; 1-byte Folded Reload
	stx	__rc14
	lda	__rc8
	eor	__rc14
	adc	__rc5
	sta	__rc21
	ldx	.Lpressure_i64_sstk+7           ; 1-byte Folded Reload
	stx	__rc15
	lda	__rc18
	eor	__rc15
	adc	__rc6
	sta	__rc23
	ldx	.Lpressure_i64_sstk+8           ; 1-byte Folded Reload
	stx	__rc18
	lda	__rc19
	eor	__rc18
	adc	__rc7
	sta	__rc20
	ldx	.Lpressure_i64_sstk+9           ; 1-byte Folded Reload
	stx	__rc19
	lda	__rc27
	eor	__rc19
	adc	__rc9
	sta	__rc27
	lda	__rc28
	ldx	.Lpressure_i64_sstk+14          ; 1-byte Folded Reload
	stx	__rc8
	eor	__rc8
	adc	__rc10
	tax
	lda	.Lpressure_i64_sstk             ; 1-byte Folded Reload
	eor	__rc11
	sta	__rc2
	lda	__rc22
	eor	__rc12
	sta	__rc3
	lda	__rc26
	eor	__rc13
	sta	__rc4
	tya
	eor	__rc14
	sta	__rc5
	lda	__rc31
	eor	__rc15
	sta	__rc6
	lda	.Lpressure_i64_sstk+2           ; 1-byte Folded Reload
	eor	__rc18
	sta	__rc7
	lda	.Lpressure_i64_sstk+1           ; 1-byte Folded Reload
	eor	__rc19
	sta	__rc9
	lda	__rc25
	eor	__rc8
	sta	__rc8
	clc
	lda	__rc29
	adc	__rc2
	tay
	lda	__rc24
	adc	__rc3
	sta	__rc10
	lda	__rc30
	adc	__rc4
	sta	__rc2
	lda	__rc21
	adc	__rc5
	sta	__rc3
	lda	__rc23
	adc	__rc6
	sta	__rc4
	lda	__rc20
	adc	__rc7
	sta	__rc5
	lda	__rc27
	adc	__rc9
	sta	__rc6
	txa
	adc	__rc8
	sta	__rc7
	ldx	__rc10
	tya
	sta	__rc16
	ldy	.Lpressure_i64_sstk+22          ; 1-byte Folded Reload
	sty	__rc31
	ldy	.Lpressure_i64_sstk+21          ; 1-byte Folded Reload
	sty	__rc30
	ldy	.Lpressure_i64_sstk+20          ; 1-byte Folded Reload
	sty	__rc29
	ldy	.Lpressure_i64_sstk+19          ; 1-byte Folded Reload
	sty	__rc28
	ldy	.Lpressure_i64_sstk+18          ; 1-byte Folded Reload
	sty	__rc27
	ldy	.Lpressure_i64_sstk+17          ; 1-byte Folded Reload
	sty	__rc26
	ldy	.Lpressure_i64_sstk+16          ; 1-byte Folded Reload
	sty	__rc25
	ldy	.Lpressure_i64_sstk+15          ; 1-byte Folded Reload
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
	.size	pressure_i64, .Lfunc_end0-pressure_i64
                                        ; -- End function
	.type	.Lstatic_stack,@object          ; @static_stack
	.section	.noinit..Lstatic_stack,"aw",@nobits
.Lstatic_stack:
	.zero	23
	.size	.Lstatic_stack, 23

.Lpressure_i64_sstk = .Lstatic_stack
	.size	.Lpressure_i64_sstk, 23
	.ident	"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"
	.section	".note.GNU-stack","",@progbits
	.addrsig
	.addrsig_sym .Lstatic_stack
	;Declaring this symbol tells the CRT that the stack pointer needs to be initialized.
	.globl	__do_init_stack
