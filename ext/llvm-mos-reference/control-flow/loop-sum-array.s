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
	.file	"loop-sum-array.c"
	.section	.text.loop_sum_array,"ax",@progbits
	.globl	loop_sum_array                  ; -- Begin function loop_sum_array
	.type	loop_sum_array,@function
loop_sum_array:                         ; @loop_sum_array
; %bb.0:
	tay
	cmp	#1
	txa
	sbc	#0
	bvc	.LBB0_2
; %bb.1:
	eor	#128
.LBB0_2:
	cmp	#0
	bmi	.LBB0_8
; %bb.3:
	sty	__rc4
	ldy	#0
	tya
	sty	__rc6
.LBB0_4:                                ; =>This Inner Loop Header: Depth=1
	clc
	ldy	#0
	adc	(__rc2),y
	sta	__rc5
	iny
	lda	__rc6
	adc	(__rc2),y
	sta	__rc6
	lda	__rc2
	clc
	adc	#2
	sta	__rc2
	lda	__rc3
	adc	#0
	sta	__rc3
	lda	#255
	dec	__rc4
	cmp	__rc4
	bne	.LBB0_6
; %bb.5:                                ;   in Loop: Header=BB0_4 Depth=1
	dex
.LBB0_6:                                ;   in Loop: Header=BB0_4 Depth=1
	lda	__rc5
	cpx	#0
	bne	.LBB0_4
; %bb.7:                                ;   in Loop: Header=BB0_4 Depth=1
	ldy	__rc4
	bne	.LBB0_4
	jmp	.LBB0_9
.LBB0_8:
	lda	#0
	tax
	stx	__rc6
.LBB0_9:
	ldx	__rc6
	rts
.Lfunc_end0:
	.size	loop_sum_array, .Lfunc_end0-loop_sum_array
                                        ; -- End function
	.ident	"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"
	.section	".note.GNU-stack","",@progbits
	.addrsig
	;Declaring this symbol tells the CRT that the stack pointer needs to be initialized.
	.globl	__do_init_stack
