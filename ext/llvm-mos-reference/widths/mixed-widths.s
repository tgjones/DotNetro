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
	.file	"mixed-widths.c"
	.section	.text.mixed_widths,"ax",@progbits
	.globl	mixed_widths                    ; -- Begin function mixed_widths
	.type	mixed_widths,@function
mixed_widths:                           ; @mixed_widths
; %bb.0:
	sta	__rc7
	txa
	ldx	__rc2
	ldy	__rc3
	clc
	adc	__rc7
	sta	__rc2
	txa
	adc	#0
	sta	__rc3
	bpl	.LBB0_2
; %bb.1:
	ldx	#255
	jmp	.LBB0_3
.LBB0_2:
	ldx	#0
.LBB0_3:
	stx	__rc8
	tya
	clc
	adc	__rc7
	tax
	lda	__rc4
	adc	#0
	sta	__rc4
	lda	__rc5
	adc	#0
	sta	__rc5
	lda	__rc6
	adc	#0
	sta	__rc6
	txa
	clc
	adc	__rc2
	tay
	lda	__rc4
	adc	__rc3
	tax
	lda	__rc5
	adc	__rc8
	sta	__rc2
	lda	__rc6
	adc	__rc8
	sta	__rc3
	tya
	rts
.Lfunc_end0:
	.size	mixed_widths, .Lfunc_end0-mixed_widths
                                        ; -- End function
	.ident	"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"
	.section	".note.GNU-stack","",@progbits
	.addrsig
	;Declaring this symbol tells the CRT that the stack pointer needs to be initialized.
	.globl	__do_init_stack
