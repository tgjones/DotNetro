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
	.file	"switch.c"
	.section	.text.switch_val,"ax",@progbits
	.globl	switch_val                      ; -- Begin function switch_val
	.type	switch_val,@function
switch_val:                             ; @switch_val
; %bb.0:
	cpx	#0
	bne	.LBB0_2
; %bb.1:
	sta	__rc2
	cmp	#4
	jmp	.LBB0_3
.LBB0_2:
	sta	__rc2
	cpx	#0
.LBB0_3:
	ldy	#255
	tya
	stx	__rc3
	bcs	.LBB0_5
; %bb.4:
	asl	__rc2
	rol	__rc3
	lda	__rc2
	asl
	tax
	lda	__rc3
	rol
	stx	__rc4
	asl	__rc4
	rol
	tax
	clc
	lda	__rc4
	adc	__rc2
	sta	__rc2
	txa
	adc	__rc3
	tax
	clc
	lda	__rc2
	adc	#10
	tay
	txa
	adc	#0
.LBB0_5:
	tax
	tya
	rts
.Lfunc_end0:
	.size	switch_val, .Lfunc_end0-switch_val
                                        ; -- End function
	.ident	"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"
	.section	".note.GNU-stack","",@progbits
	.addrsig
	;Declaring this symbol tells the CRT that the stack pointer needs to be initialized.
	.globl	__do_init_stack
