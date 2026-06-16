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
	.file	"pressure-i16.c"
	.section	.text.pressure_i16,"ax",@progbits
	.globl	pressure_i16                    ; -- Begin function pressure_i16
	.type	pressure_i16,@function
pressure_i16:                           ; @pressure_i16
; %bb.0:
	sta	__rc8
	stx	__rc9
	ldx	__rc7
	clc
	lda	__rc2
	adc	__rc8
	ldy	#0
	sty	__rc10
	sta	__rc14
	lda	__rc3
	adc	__rc9
	sta	__rc13
	lda	__rc6
	clc
	adc	__rc4
	sta	__rc12
	lda	__rc7
	adc	__rc5
	sta	__rc11
	clc
	lda	__rc4
	adc	__rc8
	sta	__rc8
	lda	__rc5
	adc	__rc9
	sta	__rc7
	clc
	lda	__rc6
	adc	__rc2
	tay
	txa
	adc	__rc3
	sta	__rc3
	tya
	eor	__rc8
	sta	__rc2
	lda	__rc12
	eor	__rc14
	sta	__rc6
	lda	__rc11
	eor	__rc13
	sta	__rc4
	lda	__rc3
	eor	__rc7
	sta	__rc3
	ldx	__rc10
	cpx	#1
	lda	__rc6
	adc	__rc2
	tay
	lda	__rc4
	adc	__rc3
	tax
	tya
	rts
.Lfunc_end0:
	.size	pressure_i16, .Lfunc_end0-pressure_i16
                                        ; -- End function
	.ident	"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"
	.section	".note.GNU-stack","",@progbits
	.addrsig
	;Declaring this symbol tells the CRT that the stack pointer needs to be initialized.
	.globl	__do_init_stack
