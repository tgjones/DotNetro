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
	.file	"struct-return-sret.c"
	.section	.text.make_big,"ax",@progbits
	.globl	make_big                        ; -- Begin function make_big
	.type	make_big,@function
make_big:                               ; @make_big
; %bb.0:
	sta	__rc5
	ldy	#0
	sta	(__rc2),y
	iny
	txa
	sta	(__rc2),y
	ldx	#1
	stx	__rc4
	tax
	iny
	lda	__rc5
	stx	__rc6
	clc
	adc	#1
	bne	.LBB0_2
; %bb.1:
	inc	__rc6
.LBB0_2:
	sta	(__rc2),y
	clc
	lda	__rc5
	adc	#2
	ldy	#1
	bcs	.LBB0_4
; %bb.3:
	ldy	#0
.LBB0_4:
	sty	__rc7
	ldy	#4
	sta	(__rc2),y
	clc
	lda	__rc5
	adc	#3
	ldy	#1
	bcs	.LBB0_6
; %bb.5:
	ldy	#0
.LBB0_6:
	sty	__rc8
	ldy	#6
	sta	(__rc2),y
	clc
	lda	__rc5
	adc	#4
	ldy	#1
	bcs	.LBB0_8
; %bb.7:
	ldy	#0
.LBB0_8:
	sty	__rc5
	ldy	#8
	sta	(__rc2),y
	ldy	__rc2
	sty	__rc9
	clc
	lda	__rc2
	adc	#2
	sta	__rc2
	ldy	__rc3
	sty	__rc10
	lda	__rc3
	adc	#0
	sta	__rc3
	ldy	__rc4
	lda	__rc6
	sta	(__rc2),y
	clc
	lda	__rc9
	adc	#4
	sta	__rc2
	lda	__rc10
	adc	#0
	sta	__rc3
	txa
	sty	__rc17
	ldy	__rc7
	cpy	#1
	ldy	__rc17
	adc	#0
	sta	(__rc2),y
	clc
	lda	__rc9
	adc	#6
	sta	__rc2
	lda	__rc10
	adc	#0
	sta	__rc3
	txa
	sty	__rc17
	ldy	__rc8
	cpy	#1
	ldy	__rc17
	adc	#0
	sta	(__rc2),y
	clc
	lda	__rc9
	adc	#8
	sta	__rc2
	lda	__rc10
	adc	#0
	sta	__rc3
	txa
	ldx	__rc5
	cpx	#1
	adc	#0
	sta	(__rc2),y
	rts
.Lfunc_end0:
	.size	make_big, .Lfunc_end0-make_big
                                        ; -- End function
	.ident	"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"
	.section	".note.GNU-stack","",@progbits
	.addrsig
	;Declaring this symbol tells the CRT that the stack pointer needs to be initialized.
	.globl	__do_init_stack
